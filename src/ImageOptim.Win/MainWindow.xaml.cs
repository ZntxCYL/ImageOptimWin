using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ImageOptim.Win.Models;
using ImageOptim.Win.Services;
using Microsoft.Win32;

namespace ImageOptim.Win;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ImageJob> _jobs = new();
    private readonly BackupManager _backups = new();
    private readonly HashSet<string> _knownPaths = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings = SettingsStore.Load();
    private CancellationTokenSource? _runCts;
    private bool _isRunning;
    private bool _checkingUpdates;
    private bool _checkAfterQueue;
    private bool _closingAfterStop;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.Apply(_settings.ThemeMode);
        JobsGrid.ItemsSource = _jobs;
        _jobs.CollectionChanged += (_, _) => { EmptyHint.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateSummary(); };
        UpdateSummary();
    }

    // Listen to changes made in Windows Settings > Personalization > Colors.
    // A WndProc hook avoids polling and works with the WPF custom title bar.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_THEMECHANGED = 0x031A;
        if (msg is WM_SETTINGCHANGE or WM_THEMECHANGED)
            Dispatcher.BeginInvoke(new Action(ThemeManager.RefreshSystemTheme));
        return IntPtr.Zero;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        int health = args.IndexOf("--update-health-token");
        if (health >= 0)
            args.RemoveRange(health, Math.Min(2, args.Count - health));
        args = args.Where(x => !x.StartsWith("-")).ToList();
        if (args.Count != 0) AddPaths(args);
        // Signal only after the main window has successfully loaded, so the
        // detached updater can roll back a version that fails to start.
        try { UpdateService.ConfirmHealthyStartup(Environment.GetCommandLineArgs()); }
        catch (IOException) { /* Updater will time out and roll back if unwritable. */ }
        catch (UnauthorizedAccessException) { }
        _ = CheckUpdatesAsync(silent: true);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is Button) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Preview/tunneling events run before DataGrid rows and cells can consume
    // file-drop routed events; this also works after the first compression run.
    private void Window_PreviewDrag(object sender, DragEventArgs e)
    {
        var isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            AddPaths(paths);
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.svg|所有文件|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var scheduled = 0;
        foreach (var file in ExpandPaths(paths))
        {
            if (!OptimizerService.IsSupported(file)) continue;
            try
            {
                var path = Path.GetFullPath(file);
                // A second drop of an already-completed file must re-queue it.
                // Never create duplicate rows for the same physical path.
                if (_knownPaths.Contains(path))
                {
                    var existing = _jobs.FirstOrDefault(j =>
                        string.Equals(j.FullPath, path, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && existing.State is
                        (JobState.Done or JobState.Unchanged or JobState.Failed or JobState.Cancelled))
                    {
                        existing.ResetForRetry();
                        scheduled++;
                    }
                    continue;
                }
                var job = new ImageJob(path);
                _knownPaths.Add(path);
                _jobs.Add(job);
                scheduled++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Ignore inaccessible or invalid paths; do not break other drops.
            }
        }
        if (scheduled > 0)
        {
            UpdateSummary();
            _ = RunQueueAsync();
        }
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) { yield return path; continue; }
            if (!Directory.Exists(path)) continue;
            IEnumerator<string>? iterator = null;
            try
            {
                iterator = Directory.EnumerateFiles(path, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }).GetEnumerator();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (iterator is null) continue;
            using (iterator)
            {
                while (true)
                {
                    string? file;
                    try { if (!iterator.MoveNext()) break; file = iterator.Current; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { break; }
                    yield return file;
                }
            }
        }
    }

    private async Task RunQueueAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        StopButton.IsEnabled = true;
        try
        {
            while (true)
            {
                if (cts.IsCancellationRequested) break;
                var job = _jobs.FirstOrDefault(x => x.State == JobState.Queued);
                if (job is null) break;
                job.State = JobState.Running;
                job.ProgressPercent = 0;
                job.ProgressDescription = "准备压缩";
                UpdateSummary();
                try
                {
                    var settingsSnapshot = _settings.Copy();
                    // Progress<T> captures the UI SynchronizationContext here, so
                    // individual row updates never modify WPF bindings off-thread.
                    var progress = new Progress<OptimizationProgress>(update =>
                    {
                        if (job.State != JobState.Running) return;
                        job.ProgressPercent = update.Percent;
                        job.ProgressDescription = update.CurrentEngine;
                    });
                    var result = await Task.Run(
                        () => OptimizerService.OptimizeAsync(job, settingsSnapshot, _backups, cts.Token, progress), cts.Token);
                    job.CurrentSize = result.FinalBytes;
                    job.EngineUsed = result.EngineName;
                    job.ErrorMessage = result.Improved ? $"使用 {result.EngineName} 完成优化" : "压缩完成，文件已是较优大小";
                    job.ProgressPercent = 100;
                    job.State = result.Improved ? JobState.Done : JobState.Unchanged;
                }
                catch (OperationCanceledException)
                {
                    job.State = JobState.Cancelled;
                    break;
                }
                catch (Exception ex)
                {
                    job.ErrorMessage = ex.Message;
                    job.State = JobState.Failed;
                }
                UpdateSummary();
            }
        }
        finally
        {
            var cancelled = cts.IsCancellationRequested;
            if (cancelled)
                foreach (var item in _jobs.Where(x => x.State == JobState.Queued)) item.State = JobState.Cancelled;
            _runCts = null;
            cts.Dispose();
            _isRunning = false;
            StopButton.IsEnabled = false;
            UpdateSummary();
            if (_closingAfterStop)
            {
                _closingAfterStop = false;
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
            else if (!cancelled && _jobs.Any(x => x.State == JobState.Queued)) _ = RunQueueAsync();
            else if (_checkAfterQueue)
            {
                _checkAfterQueue = false;
                _ = CheckUpdatesAsync(silent: true);
            }
        }
    }

    private void UpdateSummary()
    {
        if (_jobs.Count == 0)
        {
            SummaryText.Text = "拖放图像文件到上方区域";
            AgainButton.IsEnabled = false;
            return;
        }
        AgainButton.IsEnabled = !_isRunning;
        var completed = _jobs.Where(x => x.State is JobState.Done or JobState.Unchanged).ToList();
        var running = _jobs.Count(x => x.State == JobState.Running);
        var queued = _jobs.Count(x => x.State == JobState.Queued);
        var failed = _jobs.Count(x => x.State == JobState.Failed);
        if (running + queued > 0)
        {
            SummaryText.Text = $"正在压缩… {completed.Count}/{_jobs.Count} 项完成";
            return;
        }
        if (completed.Count == 0)
        {
            SummaryText.Text = failed > 0 ? $"{failed} 项失败，悬停红色 ! 查看原因" : "暂无压缩结果";
            return;
        }
        var original = completed.Sum(x => x.OriginalSize);
        var saved = completed.Sum(x => Math.Max(0, x.OriginalSize - x.CurrentSize));
        var average = completed.Average(x => x.SavingsPercent);
        var largest = completed.Max(x => x.SavingsPercent);
        SummaryText.Text = $"共节省 {SizeFormatter.Human(saved)} / {SizeFormatter.Human(original)} · " +
            $"平均 {average:0.0}% · 最高 {largest:0.0}%" +
            (failed > 0 ? $" · {failed} 项失败" : "");
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => _runCts?.Cancel();

    private void Again_Click(object sender, RoutedEventArgs e) => Retry(GetSelectedOrAll());
    private void OptimizeSelected_Click(object sender, RoutedEventArgs e) => Retry(GetSelectedOrAll());

    private List<ImageJob> GetSelectedOrAll()
    {
        var selected = JobsGrid.SelectedItems.OfType<ImageJob>().ToList();
        return selected.Count > 0 ? selected : _jobs.ToList();
    }

    private void Retry(IEnumerable<ImageJob> items)
    {
        if (_isRunning) return;
        foreach (var item in items) item.ResetForRetry();
        UpdateSummary();
        _ = RunQueueAsync();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        var selected = GetSelectedOrAll();
        foreach (var job in selected)
        {
            if (job.BackupPath is null) continue;
            try { _backups.Revert(job); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                MessageBox.Show(this, $"恢复原图失败：{job.FileName}\n{ex.Message}", "恢复原图", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        UpdateSummary();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        foreach (var job in GetSelectedOrAll())
        {
            _knownPaths.Remove(job.FullPath);
            _jobs.Remove(job);
        }
    }

    private void JobsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => ShowInExplorer();
    private void ShowInExplorer_Click(object sender, RoutedEventArgs e) => ShowInExplorer();

    private void ShowInExplorer()
    {
        if (JobsGrid.SelectedItem is not ImageJob job) return;
        if (!File.Exists(job.FullPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.FullPath}\"") { UseShellExecute = true });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
        => await CheckUpdatesAsync(silent: false);

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        try
        {
            using var service = UpdateService.CreateIfConfigured();
            if (service is null)
            {
                if (!silent) MessageBox.Show(this,
                    "尚未设置 GitHub 更新仓库及签名公钥。\n请先在构建前配置 update-config.json。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var update = await service.CheckAsync();
            if (update is null)
            {
                if (!silent) MessageBox.Show(this,
                    service.NoStableReleaseFound
                        ? "当前仓库暂无可用的正式版更新。预发布测试版不会自动安装。"
                        : "当前已是最新正式版本。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_isRunning)
            {
                if (silent) _checkAfterQueue = true;
                if (!silent) MessageBox.Show(this, "有压缩任务正在运行，完成后再安装更新。", "检查更新",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var prompt = new UpdateWindow(service, update) { Owner = this };
            if (prompt.ShowDialog() == true) Close();
        }
        catch (Exception ex)
        {
            if (!silent) MessageBox.Show(this, "检查更新失败：\n" + ex.Message, "检查更新",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checkingUpdates = false; }
    }

    private void Preferences_Click(object sender, RoutedEventArgs e)
    {
        var window = new PreferencesWindow(_settings.Copy()) { Owner = this };
        if (window.ShowDialog() != true) return;
        _settings = window.Settings;
        ThemeManager.Apply(_settings.ThemeMode);
        try { SettingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"保存偏好设置失败：\n{ex.Message}", "偏好设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            // Stop and reap child optimizers BEFORE closing, so no write can race with exit.
            _closingAfterStop = true;
            e.Cancel = true;
            _runCts?.Cancel();
            return;
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        _backups.Dispose();
    }
}

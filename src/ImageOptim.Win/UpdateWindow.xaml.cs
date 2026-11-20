using System.ComponentModel;
using System.Windows;
using ImageOptim.Win.Services;

namespace ImageOptim.Win;

public partial class UpdateWindow : Window
{
    private readonly UpdateService _service;
    private readonly AvailableUpdate _update;
    private CancellationTokenSource? _cts;
    private bool _launching;

    public UpdateWindow(UpdateService service, AvailableUpdate update)
    {
        InitializeComponent();
        _service = service;
        _update = update;
        VersionText.Text = $"当前版本 {typeof(App).Assembly.GetName().Version?.ToString(3)} → 最新版本 {update.Version.ToString(3)}\n来源：{update.Repository}";
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        InstallButton.IsEnabled = false;
        CancelButton.Content = "取消下载";
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "正在下载更新包…";
        try
        {
            var progress = new Progress<int>(percent =>
            {
                DownloadProgress.Value = percent;
                StatusText.Text = $"正在下载：{percent}%";
            });
            string file = await _service.DownloadAsync(_update, progress, _cts.Token);
            StatusText.Text = "签名和 SHA-256 校验通过。正在启动更新器…";
            UpdateService.StartUpdater(file, _update);
            _launching = true;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消下载。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "更新失败，请检查网络或发布包。";
            MessageBox.Show(this, ex.Message, "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            if (!_launching)
            {
                InstallButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                CancelButton.Content = "以后再说";
            }
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            CancelButton.IsEnabled = false;
            return;
        }
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_cts is not null && !_launching)
        {
            e.Cancel = true;
            _cts.Cancel();
        }
    }
}

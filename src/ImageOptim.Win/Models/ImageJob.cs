using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ImageOptim.Win.Models;

public enum JobState { Queued, Running, Done, Unchanged, Failed, Cancelled }

public sealed class ImageJob : INotifyPropertyChanged
{
    private JobState _state = JobState.Queued;
    private long _currentSize;
    private string _errorMessage = "";
    private string _engineUsed = "";
    private string _progressDescription = "";
    private int _progressPercent;

    public ImageJob(string path)
    {
        FullPath = Path.GetFullPath(path);
        OriginalSize = new FileInfo(FullPath).Length;
        _currentSize = OriginalSize;
    }

    public string FullPath { get; }
    public string FileName => Path.GetFileName(FullPath);
    public long OriginalSize { get; private set; }
    public string? BackupPath { get; set; }

    public string EngineUsed
    {
        get => _engineUsed;
        set { if (Set(ref _engineUsed, value)) UpdateDerived(); }
    }

    public JobState State
    {
        get => _state;
        set { if (Set(ref _state, value)) UpdateDerived(); }
    }

    public long CurrentSize
    {
        get => _currentSize;
        set { if (Set(ref _currentSize, value)) UpdateDerived(); }
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        set { if (Set(ref _progressPercent, Math.Clamp(value, 0, 100))) UpdateDerived(); }
    }

    public string ProgressDescription
    {
        get => _progressDescription;
        set { if (Set(ref _progressDescription, value)) UpdateDerived(); }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set { if (Set(ref _errorMessage, value)) UpdateDerived(); }
    }

    public double SavingsPercent => OriginalSize <= 0 ? 0 : Math.Max(0, 100.0 * (OriginalSize - CurrentSize) / OriginalSize);
    public string SizeDisplay => SizeFormatter.Human(CurrentSize);
    public string SavingsDisplay => State is JobState.Done or JobState.Unchanged
        ? $"{SavingsPercent:0.0}%" : "—";
    public Visibility ProgressVisibility => State == JobState.Running ? Visibility.Visible : Visibility.Collapsed;

    // Progress is calculated from completed optimizer stages; native optimizers do not
    // expose a consistent byte-level progress API. Never fabricate frame-by-frame values.
    public string StatusDisplay => State switch
    {
        JobState.Queued => "等待压缩",
        JobState.Running => $"{(string.IsNullOrWhiteSpace(ProgressDescription) ? "正在压缩" : ProgressDescription)} · {ProgressPercent}%",
        JobState.Done => $"完成 · {EngineUsed}",
        JobState.Unchanged => "完成 · 无需优化",
        JobState.Failed => "压缩失败（悬停查看原因）",
        JobState.Cancelled => "已取消",
        _ => ""
    };

    public string StatusTooltip => State == JobState.Failed ? ErrorMessage :
        $"{FullPath}\n{StatusDisplay}\n原始大小：{SizeFormatter.Human(OriginalSize)}\n当前大小：{SizeDisplay}\n已节省：{SavingsDisplay}";

    // Original @2x assets remain unchanged. Display variants add 4 transparent
    // pixels on all sides (40px canvas for a 32px icon). The image renders at
    // 20 DIP so its artwork stays 16 DIP without being clipped during rotation.
    public string StatusIconUri => State switch
    {
        JobState.Running => "pack://application:,,,/Assets/StatusIcons/progress-display@2x.png",
        JobState.Done => "pack://application:,,,/Assets/StatusIcons/ok-display@2x.png",
        JobState.Unchanged => "pack://application:,,,/Assets/StatusIcons/noopt-display@2x.png",
        JobState.Failed => "pack://application:,,,/Assets/StatusIcons/err-display@2x.png",
        // Original ImageOptim has no separate cancel artwork; reuse the
        // waiting icon and expose the actual status in text/tooltip.
        _ => "pack://application:,,,/Assets/StatusIcons/wait-display@2x.png"
    };

    public void ResetForRetry()
    {
        OriginalSize = File.Exists(FullPath) ? new FileInfo(FullPath).Length : 0;
        CurrentSize = OriginalSize;
        EngineUsed = "";
        ErrorMessage = "";
        ProgressDescription = "";
        ProgressPercent = 0;
        State = JobState.Queued;
        OnPropertyChanged(nameof(OriginalSize));
        UpdateDerived();
    }

    public void ResetAfterRestore()
    {
        CurrentSize = File.Exists(FullPath) ? new FileInfo(FullPath).Length : 0;
        OriginalSize = CurrentSize;
        BackupPath = null;
        ErrorMessage = "";
        EngineUsed = "";
        ProgressDescription = "";
        ProgressPercent = 100;
        State = JobState.Unchanged;
        OnPropertyChanged(nameof(OriginalSize));
        UpdateDerived();
    }

    private void UpdateDerived()
    {
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(SavingsDisplay));
        OnPropertyChanged(nameof(SavingsPercent));
        OnPropertyChanged(nameof(StatusIconUri));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTooltip));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class SizeFormatter
{
    public static string Human(long size)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double amount = size;
        var index = 0;
        while (amount >= 1024 && index < units.Length - 1) { amount /= 1024; index++; }
        return index == 0 ? $"{size} B" : $"{amount:0.##} {units[index]}";
    }
}

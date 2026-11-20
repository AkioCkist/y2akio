using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.UI.Xaml;
using y2akio.Services;
using y2akio.Services.Downloads;

namespace y2akio.Models;

public enum DownloadJobStatus
{
    Queued,
    Downloading,
    Paused,
    WaitingForNetwork,
    Completed,
    Failed,
    Canceled,
}

/// <summary>What the user asked for when a running job's token was cancelled.</summary>
internal enum StopReason { None, Pause, Cancel, Network, Shutdown }

/// <summary>
/// One entry in the download manager. Bound by the Downloads page and mutated by
/// <see cref="DownloadQueueService"/> on the UI thread only.
/// </summary>
public sealed class DownloadJob : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required DownloadRequest Request { get; set; }
    public string Title { get; set; } = "";
    public string Site { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnailSource;
    /// <summary>Lazily created bitmap for the list (x:Bind cannot convert a string to an ImageSource).</summary>
    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.ImageSource? ThumbnailSource =>
        _thumbnailSource ??= Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out var uri)
            ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri) { DecodePixelWidth = 192 }
            : null;
    public string QualityLabel { get; set; } = "";
    public string? BatchId { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public List<string> PartialFiles { get; set; } = new();

    /// <summary>Per-job speed limit in KB/s (0 = only the global limit applies).</summary>
    public int SpeedLimitKBps
    {
        get => (int)(Limiter.BytesPerSecond / 1024);
        set { Limiter.BytesPerSecond = Math.Max(0, value) * 1024L; OnPropertyChanged(); OnPropertyChanged(nameof(SpeedLimitText)); }
    }

    internal RateLimiter Limiter { get; } = new();
    internal CancellationTokenSource? Cts { get; set; }
    internal StopReason StopReason { get; set; }

    /// <summary>Raised (UI thread) for every log line of this job's yt-dlp / engine run.</summary>
    public event Action<DownloadJob, string>? LogLine;
    internal void RaiseLog(string line) => LogLine?.Invoke(this, line);

    // ── Live state ──────────────────────────────────────────────────────────

    private DownloadJobStatus _status = DownloadJobStatus.Queued;
    public DownloadJobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(PauseVisibility));
            OnPropertyChanged(nameof(ResumeVisibility));
            OnPropertyChanged(nameof(CancelVisibility));
            OnPropertyChanged(nameof(OpenVisibility));
            OnPropertyChanged(nameof(RemoveVisibility));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ProgressPaused));
            OnPropertyChanged(nameof(ProgressError));
            OnPropertyChanged(nameof(DetailText));
            OnPropertyChanged(nameof(DetailTooltip));
            OnPropertyChanged(nameof(ProgressVisibility));
            OnPropertyChanged(nameof(PercentText));
            OnPropertyChanged(nameof(PercentVisibility));
        }
    }

    private double _percent;
    public double Percent
    {
        get => _percent;
        set { if (Math.Abs(_percent - value) < 0.01) return; _percent = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentText)); OnPropertyChanged(nameof(PercentVisibility)); }
    }

    private string _statusText = "";
    /// <summary>Speed · size · ETA (engine) or yt-dlp's own progress line.</summary>
    public string StatusText { get => _statusText; set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailText)); } }

    private string _error = "";
    public string Error { get => _error; set { _error = value; OnPropertyChanged(); OnPropertyChanged(nameof(DetailText)); } }

    private string _savedPath = "";
    public string SavedPath
    {
        get => _savedPath;
        set { _savedPath = value; _savedSummary = null; OnPropertyChanged(); OnPropertyChanged(nameof(DetailText)); OnPropertyChanged(nameof(DetailTooltip)); }
    }

    private DateTime? _completedAt;
    public DateTime? CompletedAt { get => _completedAt; set { _completedAt = value; OnPropertyChanged(); } }

    private IReadOnlyList<SegmentSnapshot>? _segments;
    [JsonIgnore]
    public IReadOnlyList<SegmentSnapshot>? Segments { get => _segments; set { _segments = value; OnPropertyChanged(); } }

    private long _segmentsTotal;
    [JsonIgnore]
    public long SegmentsTotal { get => _segmentsTotal; set { _segmentsTotal = value; OnPropertyChanged(); } }

    // ── Binding helpers (no converters needed in XAML) ──────────────────────

    public bool IsActive => Status is DownloadJobStatus.Downloading;
    public bool ProgressPaused => Status is DownloadJobStatus.Paused or DownloadJobStatus.WaitingForNetwork or DownloadJobStatus.Queued;
    public bool ProgressError => Status is DownloadJobStatus.Failed or DownloadJobStatus.Canceled;

    public Visibility PauseVisibility => Vis(Status is DownloadJobStatus.Downloading or DownloadJobStatus.Queued or DownloadJobStatus.WaitingForNetwork);
    public Visibility ResumeVisibility => Vis(Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed or DownloadJobStatus.Canceled);
    public Visibility CancelVisibility => Vis(Status is not (DownloadJobStatus.Completed or DownloadJobStatus.Canceled));
    public Visibility OpenVisibility => Vis(Status is DownloadJobStatus.Completed);
    public Visibility RemoveVisibility => Vis(Status is not (DownloadJobStatus.Downloading));

    public string StatusLabel => LocalizationService.Get("Downloads.Status." + Status);

    public string DetailText => Status switch
    {
        DownloadJobStatus.Failed => Error,
        DownloadJobStatus.Completed => SavedSummary,
        DownloadJobStatus.Canceled => "",
        _ => StatusText,
    };

    /// <summary>Full error or saved path on hover (the line itself is trimmed).</summary>
    public string DetailTooltip => Status switch
    {
        DownloadJobStatus.Failed => Error,
        DownloadJobStatus.Completed => SavedPath,
        _ => StatusText,
    };

    /// <summary>"45%" while unfinished; empty when done (the bar is hidden then).</summary>
    public string PercentText => Status is DownloadJobStatus.Completed or DownloadJobStatus.Canceled || Percent <= 0
        ? ""
        : $"{Percent:0}%";

    public Visibility PercentVisibility => Vis(PercentText.Length > 0);

    /// <summary>The bar is noise once the file is saved; the card shows file name + size instead.</summary>
    public Visibility ProgressVisibility => Vis(Status is not (DownloadJobStatus.Completed or DownloadJobStatus.Canceled));

    private string? _savedSummary;
    /// <summary>"name.mp4 · 690.9 MB" for a finished job.</summary>
    private string SavedSummary => _savedSummary ??= BuildSavedSummary();

    private string BuildSavedSummary()
    {
        if (string.IsNullOrWhiteSpace(SavedPath)) return "";
        var name = System.IO.Path.GetFileName(SavedPath);
        try
        {
            var info = new System.IO.FileInfo(SavedPath);
            if (info.Exists) return $"{name} · {SegmentedDownloader.FormatBytes(info.Length)}";
        }
        catch { /* path no longer valid */ }
        return name;
    }

    public string SpeedLimitText => SpeedLimitKBps > 0 ? $"≤ {SpeedLimitKBps} KB/s" : "";

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Re-evaluates language-dependent labels after the UI language changed.</summary>
    public void RefreshLabels() => OnPropertyChanged(nameof(StatusLabel));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Serializable snapshot of a job for <c>downloads.json</c>.</summary>
public sealed class DownloadJobRecord
{
    public string Id { get; set; } = "";
    public DownloadRequest? Request { get; set; }
    public string Title { get; set; } = "";
    public string Site { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnailSource;
    /// <summary>Lazily created bitmap for the list (x:Bind cannot convert a string to an ImageSource).</summary>
    [JsonIgnore]
    public Microsoft.UI.Xaml.Media.ImageSource? ThumbnailSource =>
        _thumbnailSource ??= Uri.TryCreate(ThumbnailUrl, UriKind.Absolute, out var uri)
            ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri) { DecodePixelWidth = 192 }
            : null;
    public string QualityLabel { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DownloadJobStatus Status { get; set; }
    public double Percent { get; set; }
    public string StatusText { get; set; } = "";
    public string Error { get; set; } = "";
    public string SavedPath { get; set; } = "";
    public int SpeedLimitKBps { get; set; }
    public List<string> PartialFiles { get; set; } = new();
}

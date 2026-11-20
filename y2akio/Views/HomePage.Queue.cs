using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Follows the queued jobs this page started, and the clipboard link offer.</summary>
public sealed partial class HomePage : Page
{
    private List<DownloadJob> _trackedJobs = new();

    /// <summary>Shows the combined progress and log of <paramref name="jobs"/> (one video or a playlist batch).</summary>
    private void TrackJobs(IReadOnlyList<DownloadJob> jobs)
    {
        UntrackJobs();
        _trackedJobs = jobs.ToList();

        ErrorBar.IsOpen = false;
        SuccessBar.IsOpen = false;
        ShowLogPanel();
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadStatusText.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;

        foreach (var job in _trackedJobs)
        {
            job.PropertyChanged += OnTrackedJobChanged;
            job.LogLine += OnTrackedJobLog;
        }
        AppendLog(_trackedJobs.Count == 1
            ? $"Queued: {_trackedJobs[0].Title}"
            : $"Queued {_trackedJobs.Count} videos (see Downloads for each one).");
        UpdateTrackedProgress();
    }

    private void UntrackJobs()
    {
        foreach (var job in _trackedJobs)
        {
            job.PropertyChanged -= OnTrackedJobChanged;
            job.LogLine -= OnTrackedJobLog;
        }
        _trackedJobs = new List<DownloadJob>();
    }

    private void OnTrackedJobLog(DownloadJob job, string line)
    {
        if (_trackedJobs.Count > 1)
            line = $"[{_trackedJobs.IndexOf(job) + 1}/{_trackedJobs.Count}] {line}";
        AppendLog(line);
    }

    private void OnTrackedJobChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadJob.Percent) or nameof(DownloadJob.Status) or nameof(DownloadJob.StatusText))
            UpdateTrackedProgress();
    }

    private static bool IsFinished(DownloadJob j) =>
        j.Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Canceled;

    private void UpdateTrackedProgress()
    {
        if (_trackedJobs.Count == 0) return;

        DownloadProgressBar.Value = _trackedJobs.Average(j => j.Status == DownloadJobStatus.Completed ? 100 : j.Percent);

        var finished = _trackedJobs.Count(IsFinished);
        if (_trackedJobs.Count == 1)
        {
            var job = _trackedJobs[0];
            DownloadStatusText.Text = job.Status == DownloadJobStatus.Downloading && job.StatusText.Length > 0
                ? job.StatusText
                : job.StatusLabel;
        }
        else
        {
            var running = _trackedJobs.FirstOrDefault(j => j.Status == DownloadJobStatus.Downloading);
            var label = running?.Title ?? _trackedJobs.FirstOrDefault(j => !IsFinished(j))?.StatusLabel ?? "";
            DownloadStatusText.Text = $"({finished}/{_trackedJobs.Count}) {label}";
        }

        if (finished < _trackedJobs.Count) return;

        // Everything this page queued is done: report once and stop following.
        var completed = _trackedJobs.Where(j => j.Status == DownloadJobStatus.Completed).ToList();
        var failed = _trackedJobs.Where(j => j.Status == DownloadJobStatus.Failed).ToList();
        foreach (var job in completed) AppendLog($"Saved: {job.SavedPath}");

        if (completed.Count > 0)
        {
            SuccessBar.Message = completed.Count == 1 && _trackedJobs.Count == 1
                ? $"{L("Home.SavedPrefix")} {completed[0].Title}"
                : string.Format(L("Home.BatchSaved"), completed.Count, _trackedJobs.Count);
            SuccessBar.IsOpen = true;
        }
        if (failed.Count > 0)
        {
            foreach (var job in failed) AppendLog($"ERROR ({job.Title}): {job.Error}");
            ErrorBar.Message = failed.Count == 1 ? failed[0].Error : string.Format(L("Home.BatchFailed"), failed.Count, string.Join(", ", failed.Select(j => j.Title)));
            ErrorBar.Title = L("Home.DownloadFailedTitle");
            ErrorBar.IsOpen = true;
        }
        UntrackJobs();
    }

    // ─── Clipboard link offer ────────────────────────────────────────────────

    private void OnClipboardLinkDetected(string url) => DispatcherQueue.TryEnqueue(ShowPendingClipboardLink);

    private void ShowPendingClipboardLink()
    {
        if (ClipboardWatcher.PendingUrl is not { } url || url == UrlTextBox.Text?.Trim()) return;
        ClipboardBar.Title = L("Home.ClipboardTitle");
        ClipboardBar.Message = url;
        ClipboardAnalyzeButton.Content = L("Home.Analyze");
        ClipboardBar.IsOpen = true;
    }

    private void ClipboardAnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (ClipboardWatcher.PendingUrl is not { } url) return;
        ClipboardWatcher.Consume();
        ClipboardBar.IsOpen = false;
        UrlTextBox.Text = url;
        _ = AnalyzeAsync();
    }

    private void ClipboardBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => ClipboardWatcher.Consume();
}

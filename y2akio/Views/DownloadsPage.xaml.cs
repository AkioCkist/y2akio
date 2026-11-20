using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>The download manager: every queued / running / finished job with IDM-style controls.</summary>
public sealed partial class DownloadsPage : Page
{
    private static DownloadQueueService Queue => DownloadQueueService.Instance;
    private bool _loading = true;

    public DownloadsPage()
    {
        InitializeComponent();
        JobsList.ItemsSource = Queue.Jobs;

        SpeedLimitBox.Value = AppSettingsService.Instance.SpeedLimitKBps;
        ScheduleCheck.IsChecked = Queue.ScheduledStart is not null;
        ScheduleTimePicker.SelectedTime = (Queue.ScheduledStart ?? DateTime.Now.AddHours(1)).TimeOfDay;
        ScheduleTimePicker.IsEnabled = ScheduleCheck.IsChecked == true;
        WhenDoneComboBox.SelectedIndex = (int)Queue.WhenDone;
        ApplyLanguage();
        _loading = false;

        Queue.QueueChanged += OnQueueChanged;
        Queue.Jobs.CollectionChanged += OnJobsChanged;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) =>
        {
            Queue.QueueChanged -= OnQueueChanged;
            Queue.Jobs.CollectionChanged -= OnJobsChanged;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        };
        UpdateSummary();
    }

    private static string L(string key) => LocalizationService.Get(key);

    private void OnLanguageChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyLanguage);
    private void OnQueueChanged() => DispatcherQueue.TryEnqueue(UpdateSummary);
    private void OnJobsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateSummary();

    private void ApplyLanguage()
    {
        PageTitle.Text = L("Downloads.Title");
        ResumeAllText.Text = L("Downloads.ResumeAll");
        PauseAllText.Text = L("Downloads.PauseAll");
        ClearFinishedText.Text = L("Downloads.ClearFinished");
        SpeedLimitLabel.Text = L("Downloads.SpeedLimit");
        ScheduleLabel.Text = L("Downloads.StartAtLabel");
        ToolTipService.SetToolTip(ScheduleCheck, L("Downloads.StartAt"));
        WhenDoneLabel.Text = L("Downloads.WhenDone");
        WhenDoneNothingItem.Content = L("Downloads.WhenDoneNothing");
        WhenDoneSleepItem.Content = L("Downloads.WhenDoneSleep");
        WhenDoneShutdownItem.Content = L("Downloads.WhenDoneShutdown");
        EmptyText.Text = L("Downloads.Empty");
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var jobs = Queue.Jobs;
        EmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var active = jobs.Count(j => j.Status == DownloadJobStatus.Downloading);
        var queued = jobs.Count(j => j.Status is DownloadJobStatus.Queued or DownloadJobStatus.WaitingForNetwork);
        var done = jobs.Count(j => j.Status == DownloadJobStatus.Completed);
        var text = string.Format(L("Downloads.Summary"), active, queued, done);
        if (Queue.ScheduledStart is { } at)
            text += " · " + string.Format(L("Downloads.ScheduledFor"), at.ToString("HH:mm"));
        SummaryText.Text = text;
    }

    // ─── Toolbar ─────────────────────────────────────────────────────────────

    private void ResumeAllButton_Click(object sender, RoutedEventArgs e) => Queue.ResumeAll();
    private void PauseAllButton_Click(object sender, RoutedEventArgs e) => Queue.PauseAll();
    private void ClearFinishedButton_Click(object sender, RoutedEventArgs e) => Queue.ClearFinished();

    private void SpeedLimitBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        var value = double.IsNaN(sender.Value) ? 0 : (int)Math.Max(0, sender.Value);
        AppSettingsService.Instance.UpdateDownloadSettings(d => d.SpeedLimitKBps = value);
    }

    private void Schedule_Changed(object sender, RoutedEventArgs e)
    {
        ScheduleTimePicker.IsEnabled = ScheduleCheck.IsChecked == true;
        ApplySchedule();
    }
    private void ScheduleTimePicker_TimeChanged(object? sender, TimePickerValueChangedEventArgs e) => ApplySchedule();

    private void ApplySchedule()
    {
        if (_loading) return;
        if (ScheduleCheck.IsChecked != true || ScheduleTimePicker.SelectedTime is not { } time)
        {
            Queue.Schedule(null);
            return;
        }
        var at = DateTime.Today + time;
        if (at <= DateTime.Now) at = at.AddDays(1); // a time already past today means tomorrow
        Queue.Schedule(at);
    }

    private void WhenDoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Queue.WhenDone = WhenDoneComboBox.SelectedIndex switch
        {
            1 => WhenDoneAction.Sleep,
            2 => WhenDoneAction.Shutdown,
            _ => WhenDoneAction.None,
        };
    }

    // ─── Per-job commands ────────────────────────────────────────────────────

    private static DownloadJob? JobOf(object sender) => (sender as FrameworkElement)?.Tag as DownloadJob;

    private void PauseJob_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } j) Queue.Pause(j); }
    private void ResumeJob_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } j) Queue.Resume(j); }
    private void CancelJob_Click(object sender, RoutedEventArgs e) { if (JobOf(sender) is { } j) Queue.Cancel(j); }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is { } j && File.Exists(j.SavedPath))
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(j.SavedPath));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (JobOf(sender) is not { } j) return;
        var folder = File.Exists(j.SavedPath) ? Path.GetDirectoryName(j.SavedPath) : j.Request.SaveFolder;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            _ = Windows.System.Launcher.LaunchFolderPathAsync(folder);
    }

    /// <summary>The "..." menu is built on open: per-job speed limit, copy link, remove.</summary>
    private void JobMenu_Opening(object sender, object e)
    {
        if (sender is not MenuFlyout menu || (menu.Target as FrameworkElement)?.Tag is not DownloadJob job) return;
        menu.Items.Clear();

        var limit = new MenuFlyoutSubItem { Text = L("Downloads.JobSpeedLimit") };
        foreach (var kbps in new[] { 0, 256, 512, 1024, 2048, 5120, 10240 })
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = kbps == 0 ? L("Downloads.Unlimited") : kbps >= 1024 ? $"{kbps / 1024} MB/s" : $"{kbps} KB/s",
                IsChecked = job.SpeedLimitKBps == kbps,
            };
            item.Click += (_, _) => job.SpeedLimitKBps = kbps;
            limit.Items.Add(item);
        }
        menu.Items.Add(limit);

        var copy = new MenuFlyoutItem { Text = L("Downloads.CopyLink"), Icon = new FontIcon { Glyph = "" } };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(job.Request.Url);
            ClipboardWatcher.MarkSeen(job.Request.Url); // our own copy is not a "new link"
            Clipboard.SetContent(package);
        };
        menu.Items.Add(copy);

        if (job.Status != DownloadJobStatus.Downloading)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = L("Downloads.Remove"), Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => Queue.Remove(job);
            menu.Items.Add(remove);
        }
    }
}

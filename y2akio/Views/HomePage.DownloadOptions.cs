using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using y2akio;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Download option binding, validation, and execution.</summary>
public sealed partial class HomePage : Page
{
    private void PopulateOptionsPanel(VideoInfo video)
    {
        VideoTitleText.Text = video.Title;
        UploaderText.Text = video.Uploader;
        DurationText.Text = video.DurationDisplay;

        if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl) && Uri.TryCreate(video.ThumbnailUrl, UriKind.Absolute, out var thumbUri))
            ThumbnailImage.Source = new BitmapImage(thumbUri);

        PopulateSubtitleLanguages(video);
        SubtitleOptionsPanel.Visibility = SubtitlesCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshQualityOptions();
    }

    private void PopulateSubtitleLanguages(VideoInfo video)
    {
        // Manual (author-uploaded): single-select dropdown. "all" = every manual track.
        SubtitleLanguageComboBox.Items.Clear();
        SubtitleLanguageComboBox.Items.Add(new SubtitleLanguage("all", "All languages (auto)"));
        foreach (var (code, name) in video.ManualSubtitleLanguages.OrderBy(p => p.Value))
            SubtitleLanguageComboBox.Items.Add(new SubtitleLanguage(code, $"{name} ({code})"));
        SubtitleLanguageComboBox.Items.Add(new SubtitleLanguage("none", "None"));
        SubtitleLanguageComboBox.SelectedIndex = 0;

        // Auto-generated: multi-select checklist.
        _autoSubtitleLangs = new ObservableCollection<SubtitleSelection>();
        foreach (var (code, name) in video.AutoSubtitleLanguages.OrderBy(p => p.Value))
            _autoSubtitleLangs.Add(new SubtitleSelection(code, $"{name} ({code})"));
        AutoLanguageList.ItemsSource = _autoSubtitleLangs;
    }

    private void SubtitlesCheck_Toggled(object sender, RoutedEventArgs e)
    {
        SubtitleOptionsPanel.Visibility = SubtitlesCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SubtitleSource_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before all fields are wired; skip until layout done.
        if (ManualLanguagePanel is null || AutoLanguagePanel is null) return;
        ManualLanguagePanel.Visibility = SubtitleManualCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AutoLanguagePanel.Visibility = SubtitleAutoCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ManualSelectAllButton_Click(object sender, RoutedEventArgs e) => SubtitleLanguageComboBox.SelectedIndex = 0; // "all"

    private void ManualDeselectAllButton_Click(object sender, RoutedEventArgs e) =>
        SubtitleLanguageComboBox.SelectedIndex = SubtitleLanguageComboBox.Items.Count - 1; // "none"

    private void AutoSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoSubtitleLangs is null) return;
        foreach (var lang in _autoSubtitleLangs) lang.Selected = true;
    }

    private void AutoDeselectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_autoSubtitleLangs is null) return;
        foreach (var lang in _autoSubtitleLangs) lang.Selected = false;
    }

    private void FileFormatToggle_Click(object sender, RoutedEventArgs e)
    {
        var isVideo = ReferenceEquals(sender, VideoFormatToggle);
        VideoFormatToggle.IsChecked = isVideo;
        AudioFormatToggle.IsChecked = !isVideo;
        ContainerFormatPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        CodecPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        AudioFormatPanel.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;
        SubtitleSection.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

        // Toggle which save location row is shown
        VideoSaveLocationRow.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        AudioSaveLocationRow.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        RefreshQualityOptions();
    }

    private void AudioFormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshQualityOptions();

    private void VideoCodecComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before ContainerComboBox is wired — guard.
        if (ContainerComboBox is null) return;
        var codec = (VideoCodecComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        ContainerComboBox.IsEnabled = codec == "auto";
    }

    /// <summary>Rebuilds the Quality/Format dropdown based on the selected media type and the analyzed formats.</summary>
    private void RefreshQualityOptions()
    {
        QualityComboBox.Items.Clear();
        if (_currentVideo is null) return;

        bool wantVideo = VideoFormatToggle.IsChecked == true;

        if (wantVideo)
        {
            QualityComboBox.Items.Add("Best Quality");
            var heights = _currentVideo.Formats
                .Where(f => f.HasVideo && f.Height.HasValue)
                .Select(f => f.Height!.Value)
                .Distinct()
                .OrderByDescending(h => h);

            foreach (var h in heights)
                QualityComboBox.Items.Add($"{h}p");
        }
        else
        {
            QualityComboBox.Items.Add("Best Quality");
            if (!IsLosslessAudioFormat())
            {
                foreach (var kbps in new[] { 320, 256, 192, 128 })
                    QualityComboBox.Items.Add($"{kbps} kbps");
            }
        }

        QualityComboBox.SelectedIndex = 0;
    }

    private bool IsLosslessAudioFormat()
    {
        var tag = (AudioFormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag is "flac" or "wav" or "alac";
    }

    // ─── Folder pickers ──────────────────────────────────────────────────────

    private async void ChooseVideoFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
            VideoSaveLocationTextBox.Text = folder.Path;
    }

    private async void ChooseAudioFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
            AudioSaveLocationTextBox.Text = folder.Path;
    }

    private static async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");
        return await picker.PickSingleFolderAsync();
    }

    // ─── Download ─────────────────────────────────────────────────────────────

    private DownloadRequest BuildDownloadRequest(string url, bool isVideo, string qualitySelection, string cookiesPath)
    {
        int? targetHeight = null;
        int? audioKbps = null;

        if (isVideo)
        {
            if (qualitySelection.EndsWith('p') && int.TryParse(qualitySelection.TrimEnd('p'), out var h))
                targetHeight = h;
        }
        else
        {
            var digits = new string(qualitySelection.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var kbps)) audioKbps = kbps;
        }

        // Use the correct save folder for the chosen media type
        var saveFolder = isVideo ? VideoSaveLocationTextBox.Text : AudioSaveLocationTextBox.Text;

        // Non-auto codec forces a transcode → the target container must be MP4 (all codecs here mux into MP4).
        var videoCodec = (VideoCodecComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        var container = videoCodec == "auto"
            ? (ContainerComboBox.SelectedItem as string ?? "MP4").ToLowerInvariant()
            : "mp4";

        return new DownloadRequest
        {
            Url = url,
            Kind = isVideo ? MediaKind.Video : MediaKind.Audio,
            SaveFolder = saveFolder,
            CookiesPath = cookiesPath,
            TargetHeight = targetHeight,
            VideoCodec = videoCodec,
            ContainerFormat = container,
            AudioBitrateKbps = audioKbps,
            AudioFormat = (AudioFormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mp3",
            PrefixDateTime = PrefixDateTimeCheck.IsChecked == true,
            PrefixChannel = PrefixChannelCheck.IsChecked == true,
            IncludeTitle = IncludeTitleCheck.IsChecked == true,
            SubtitlesEnabled = SubtitlesCheck.IsChecked == true,
            SubtitleManual = ResolveSubtitleManualEnabled(),
            SubtitleAuto = ResolveSubtitleAutoEnabled(),
            SubtitleLangCode = ResolveSubtitleLangCodes(),
            SubtitleFormat = (SubtitleFormatComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToLowerInvariant() ?? "srt",
            EmbedSubtitles = SubtitleEmbedRadio.IsChecked == true,
            EmbedMetadata = EmbedMetadataCheck.IsChecked == true,
            ThumbnailMode = ThumbnailComboBox.SelectedIndex switch
            {
                0 => "embed",
                1 => "save",
                _ => "none",
            },
        };
    }

    private bool ResolveSubtitleManualEnabled() =>
        SubtitlesCheck.IsChecked == true
        && SubtitleManualCheck.IsChecked == true
        && SubtitleLanguageComboBox.SelectedItem is SubtitleLanguage manual
        && manual.Code != "none";

    private bool ResolveSubtitleAutoEnabled() =>
        SubtitlesCheck.IsChecked == true
        && SubtitleAutoCheck.IsChecked == true
        && _autoSubtitleLangs?.Any(l => l.Selected) == true;

    /// <summary>
    /// Builds the comma-separated --sub-langs value. Manual is a single dropdown choice (or "all" for
    /// every manual track); auto appends each ticked auto language. "all" alone covers everything.
    /// </summary>
    private string ResolveSubtitleLangCodes()
    {
        var codes = new List<string>();

        if (SubtitleManualCheck.IsChecked == true
            && SubtitleLanguageComboBox.SelectedItem is SubtitleLanguage manual
            && manual.Code != "none")
        {
            if (manual.Code == "all")
                return "all"; // grabs every track, manual and auto
            codes.Add(manual.Code);
        }

        if (SubtitleAutoCheck.IsChecked == true && _autoSubtitleLangs is { } autoLangs)
            codes.AddRange(autoLangs.Where(l => l.Selected).Select(l => l.Code));

        return codes.Count == 0 ? "all" : string.Join(",", codes);
    }

    private async void DownloadNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";
        string cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();
        var request = BuildDownloadRequest(_currentVideo.OriginalUrl, isVideo, qualitySelection, cookiesPath);

        _activeDownloads++;
        DownloadNowButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadStatusText.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        _downloadCts = new CancellationTokenSource();

        ShowLogPanel();
        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.Percent;
            DownloadStatusText.Text = p.StatusText;
        });

        try
        {
            var savedPath = await _ytDlp.DownloadAsync(request, progress, _downloadCts.Token, AppendLog);
            AppendLog($"Saved: {savedPath}");
            ToastService.ShowDownloadComplete(_currentVideo.Title, savedPath);
            ShowDownloadComplete($"Video saved: {_currentVideo.Title}");

            var historyItem = new DownloadHistoryItem
            {
                Title = _currentVideo.Title,
                Kind = request.Kind,
                QualityLabel = qualitySelection,
                FilePath = savedPath,
                FolderPath = request.SaveFolder,
                CompletedAt = DateTime.Now,
            };
            _history.Insert(0, historyItem);
            await _historyStore.SaveAsync(_history);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(cookiesPath)) AppSettingsService.Instance.CleanupCookiesFile();
            _activeDownloads--;
            DownloadNowButton.IsEnabled = _activeDownloads == 0;
        }
    }

    // ─── History actions ─────────────────────────────────────────────────────

}

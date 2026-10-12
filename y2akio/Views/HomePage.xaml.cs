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
using y2akio.Helpers;
using y2akio.Models;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Home page: the YT-DLP downloader (URL analyze, options, playlist, log, history).</summary>
public sealed partial class HomePage : Page
{
    private readonly YtDlpService _ytDlp = new(
        ytDlpPath: ResolveYtDlpPath(),
        ffmpegDirectory: ResolveFfmpegDirectory(),
        denoPath: ExecutableLocator.Find("deno.exe", Path.Combine("bin", "deno"), "bin", "") ?? "");
    private readonly HistoryStore _historyStore = new();
    private readonly ObservableCollection<DownloadHistoryItem> _history = new();

    private VideoInfo? _currentVideo;
    private PlaylistInfo? _currentPlaylist;
    private CancellationTokenSource? _downloadCts;
    private ObservableCollection<SubtitleSelection>? _autoSubtitleLangs;

    private bool _analyzeInProgress;   // guards Enter+click / double-click races while an analyze runs
    private bool _previewActive;       // instant card is shown but the full dump hasn't populated options yet
    private int _activeDownloads;      // so an analyze finish never re-enables DownloadNow mid-download

    private readonly TikTokPhotoService _tikTok;
    private readonly FacebookStoryService _facebookStories = new();
    private readonly InstagramStoryService _instagramStories = new();
    private readonly FacebookPostService _facebookPosts = new();
    private readonly InstagramPostService _instagramPosts = new();
    private readonly XPostService _xPosts = new();
    private MediaGalleryPost? _currentGalleryPost;
    private CancellationTokenSource? _galleryLoadCts; // cancels thumbnail loading when another post replaces it
    private bool _galleryDownloading;

    /// <summary>
    /// Looks for the bundled yt-dlp first (so it works after you publish/copy the app elsewhere),
    /// then PATH, then finally just "yt-dlp.exe". The bundle is the onedir build (bin\yt-dlp\ with
    /// _internal\): the onefile exe re-extracts ~40MB to %TEMP% on every run, which antivirus
    /// rescans each time on machines that haven't seen it — analyze took ages there.
    /// </summary>
    private static string ResolveYtDlpPath()
        => ExecutableLocator.Find("yt-dlp.exe", Path.Combine("bin", "yt-dlp"), "bin", "") ?? "yt-dlp.exe";

    private static string ResolveFfmpegDirectory()
    {
        var executable = ExecutableLocator.Find(
            "ffmpeg.exe",
            Path.Combine("bin", "ffmpeg-9.0.1-full_build", "bin"),
            "bin",
            "");
        return executable is null ? "" : Path.GetDirectoryName(executable) ?? "";
    }

    public HomePage()
    {
        _tikTok = new(_ytDlp.FetchPagesAsync);
        InitializeComponent();

        HistoryList.ItemsSource = _history;

        // Load default save folders from persisted settings
        VideoSaveLocationTextBox.Text = AppSettingsService.Instance.VideoSaveFolder;
        AudioSaveLocationTextBox.Text = AppSettingsService.Instance.AudioSaveFolder;

        ToastService.Log = msg => AppendLog(msg);

        _ = LoadHistoryAsync();

        // Apply current language on page load
        ApplyLanguage();

        // React to language changes (from SettingsPage or elsewhere)
        LocalizationService.LanguageChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(ApplyLanguage);

        // React to save-folder changes made in SettingsPage
        AppSettingsService.Instance.SettingsChanged += (_, args) =>
        {
            if (!args.SaveFolderChanged) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                VideoSaveLocationTextBox.Text = AppSettingsService.Instance.VideoSaveFolder;
                AudioSaveLocationTextBox.Text = AppSettingsService.Instance.AudioSaveFolder;
            });
        };
    }

    // ─── Localization ─────────────────────────────────────────────────────────

    private void ApplyLanguage()
    {
        // Header
        SubtitleText.Text = L("Home.Subtitle");

        // URL bar
        UrlTextBox.PlaceholderText = L("Home.UrlPlaceholder");
        AnalyzeButton.Content = L("Home.Analyze");

        // Info bars
        ErrorBar.Title = L("Home.ErrorTitle");
        SuccessBar.Title = L("Home.SuccessTitle");

        // Download options section
        DownloadOptionsTitle.Text = L("Home.DownloadOptions");
        FileFormatLabel.Text = L("Home.FileFormat");
        VideoFormatToggle.Content = L("Home.VideoMp4");
        AudioFormatToggle.Content = L("Home.AudioMusic");
        QualityLabel.Text = L("Home.Quality");
        VideoFormatLabel.Text = L("Home.VideoFormat");
        AudioFormatLabel.Text = L("Home.AudioFormat");

        // Rebuild codec combo (localized item texts)
        var codecIdx = VideoCodecComboBox.SelectedIndex;
        VideoCodecComboBox.Items.Clear();
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "auto", Content = L("Home.CodecAuto") });
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "h264", Content = L("Home.CodecH264") });
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "hevc", Content = L("Home.CodecHevc") });
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "av1", Content = L("Home.CodecAv1") });
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "vp9", Content = L("Home.CodecVp9") });
        VideoCodecComboBox.Items.Add(new ComboBoxItem { Tag = "mpeg4", Content = L("Home.CodecMpeg4") });
        VideoCodecComboBox.SelectedIndex = codecIdx >= 0 ? codecIdx : 0;
        VideoCodecLabel.Text = L("Home.VideoCodec");

        // File name prefix
        FileNamePrefixLabel.Text = L("Home.FileNamePrefix");
        PrefixDateTimeCheck.Content = L("Home.PrefixDateTime");
        PrefixChannelCheck.Content = L("Home.PrefixChannel");
        IncludeTitleCheck.Content = L("Home.IncludeTitle");

        // Subtitles
        SubtitlesCheck.Content = L("Home.DownloadSubtitles");
        SubtitleOptionsTitle.Text = L("Home.SubtitleOptions");
        SubtitleEmbedRadio.Content = L("Home.SubtitleEmbed");
        SubtitleSeparateRadio.Content = L("Home.SubtitleSeparate");
        IncludeTracksLabel.Text = L("Home.IncludeTracks");
        SubtitleManualCheck.Content = L("Home.ManualSubs");
        SubtitleAutoCheck.Content = L("Home.AutoSubs");
        AutoLanguagesLabel.Text = L("Home.Languages");
        ManualSelectAllButton.Content = L("Home.SelectAll");
        ManualDeselectAllButton.Content = L("Home.DeselectAll");
        AutoSelectAllButton.Content = L("Home.SelectAll");
        AutoDeselectAllButton.Content = L("Home.DeselectAll");
        SubtitleFormatLabel.Text = L("Home.SubtitleFormat");

        // Save locations
        VideoSaveLocationLabel.Text = L("Home.VideoSaveLocation");
        AudioSaveLocationLabel.Text = L("Home.AudioSaveLocation");
        ChooseVideoFolderButton.Content = L("Home.ChooseFolder");
        ChooseAudioFolderButton.Content = L("Home.ChooseFolder");

        // Extra options
        ExtraOptionsLabel.Text = L("Home.ExtraOptions");
        EmbedMetadataCheck.Content = L("Home.EmbedMetadata");
        ThumbnailLabel.Text = L("Home.Thumbnail");

        // Rebuild ThumbnailComboBox items (x:String items can't be updated in place)
        var thumbIdx = ThumbnailComboBox.SelectedIndex;
        ThumbnailComboBox.Items.Clear();
        ThumbnailComboBox.Items.Add(L("Home.ThumbnailEmbed"));
        ThumbnailComboBox.Items.Add(L("Home.ThumbnailSave"));
        ThumbnailComboBox.Items.Add(L("Home.ThumbnailNone"));
        ThumbnailComboBox.SelectedIndex = thumbIdx >= 0 ? thumbIdx : 0;

        // Download button
        DownloadNowButton.Content = L("Home.DownloadNow");

        // Log section
        DownloadLogTitle.Text = L("Home.DownloadLog");
        ClearLogButton.Content = L("Home.Clear");

        // History section
        DownloadHistoryTitle.Text = L("Home.DownloadHistory");
        ClearHistoryButton.Content = L("Home.ClearAll");

        // Playlist button
        UpdateDownloadPlaylistButton();

        // TikTok photo gallery
        GallerySelectAllButton.Content = L("Home.SelectAll");
        GalleryDeselectAllButton.Content = L("Home.DeselectAll");
        GalleryHint.Text = L("Home.GalleryHint");
        ToolTipService.SetToolTip(ChooseGalleryFolderButton, L("Home.GallerySaveLocation"));
        ToolTipService.SetToolTip(GallerySaveModeComboBox, L("Home.GallerySaveAs"));
        GallerySaveFolderItem.Content = L("Home.GallerySaveFolder");
        GallerySaveZipItem.Content = L("Home.GallerySaveZip");
        UpdateGalleryButtons();
    }

    private static string L(string key) => LocalizationService.Get(key);

    // ─── History ──────────────────────────────────────────────────────────────

    private async Task LoadHistoryAsync()
    {
        var items = await _historyStore.LoadAsync();
        foreach (var item in items.OrderByDescending(i => i.CompletedAt))
            _history.Add(item);
    }

    // ─── URL analysis ────────────────────────────────────────────────────────

    private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = AnalyzeAsync();
    }

    private void AnalyzeButton_Click(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        var url = UrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url) || _analyzeInProgress)
            return;

        _analyzeInProgress = true;
        ErrorBar.IsOpen = false;
        OptionsPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        if (!_galleryDownloading) GalleryPanel.Visibility = Visibility.Collapsed;
        AnalyzingRing.IsActive = true;
        AnalyzeButton.IsEnabled = false;

        string cookiesPath = "";
        try
        {
            // TikTok photo posts (yt-dlp only extracts their audio), Facebook / Instagram photo posts and
            // stories (photos unsupported / login-only in yt-dlp) never reach yt-dlp — they get the
            // pick-and-save gallery instead.
            if (await TryFetchGalleryPostAsync(url) is { } galleryPost)
            {
                ShowGalleryPost(galleryPost);
                return;
            }

            cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();

            if (YtDlpService.LooksLikeSingleVideoUrl(url))
            {
                // Fast single-video path: ONE full dump (~4s). Paint the card from oEmbed (~0.2s) while it runs.
                _currentVideo = null;
                _previewActive = true;
                var dumpTask = _ytDlp.AnalyzeVideoAsync(url, cookiesPath);
                _ = TryPaintYouTubePreviewAsync(url);
                var video = await dumpTask;
                FinishVideoOptions(video);
            }
            else
            {
                // Ambiguous / playlist URL: probe first to detect a playlist, then populate.
                var result = await _ytDlp.AnalyzeAsync(url, cookiesPath);

                if (result.IsPlaylist && result.Playlist is { } playlist)
                {
                    _currentPlaylist = playlist;
                    PopulatePlaylistPanel(playlist);
                    PlaylistPanel.Visibility = Visibility.Visible;
                }
                else if (result.Video is { } video)
                {
                    FinishVideoOptions(video);
                }
            }
        }
        catch (Exception ex)
        {
            HidePreviewOnError();
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(cookiesPath)) AppSettingsService.Instance.CleanupCookiesFile();
            AnalyzingRing.IsActive = false;
            AnalyzeButton.IsEnabled = true;
            _analyzeInProgress = false;
        }
    }

    // ─── Playlist ─────────────────────────────────────────────────────────────

}

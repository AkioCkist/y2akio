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
    private static YtDlpService _ytDlp => AppServices.YtDlp;
    private static System.Collections.ObjectModel.ObservableCollection<DownloadHistoryItem> _history => HistoryService.Instance.Items;

    private VideoInfo? _currentVideo;
    private PlaylistInfo? _currentPlaylist;
    private ObservableCollection<SubtitleSelection>? _autoSubtitleLangs;

    private bool _analyzeInProgress;   // guards Enter+click / double-click races while an analyze runs
    private bool _previewActive;       // instant card is shown but the full dump hasn't populated options yet

    private readonly TikTokPhotoService _tikTok;
    private readonly FacebookStoryService _facebookStories = new();
    private readonly InstagramStoryService _instagramStories = new();
    private readonly FacebookPostService _facebookPosts = new();
    private readonly InstagramPostService _instagramPosts = new();
    private readonly XPostService _xPosts = new();
    private MediaGalleryPost? _currentGalleryPost;
    private CancellationTokenSource? _galleryLoadCts; // cancels thumbnail loading when another post replaces it
    private bool _galleryDownloading;

    public HomePage()
    {
        _tikTok = new(_ytDlp.FetchPagesAsync);
        InitializeComponent();

        InitHistoryView();
        Unloaded += (_, _) => UntrackJobs();

        // Load default save folders from persisted settings
        VideoSaveLocationTextBox.Text = AppSettingsService.Instance.VideoSaveFolder;
        AudioSaveLocationTextBox.Text = AppSettingsService.Instance.AudioSaveFolder;

        ToastService.Log = msg => AppendLog(msg);

        _ = HistoryService.Instance.EnsureLoadedAsync();
        ShowPendingClipboardLink();
        ClipboardWatcher.LinkDetected += OnClipboardLinkDetected;
        Unloaded += (_, _) => ClipboardWatcher.LinkDetected -= OnClipboardLinkDetected;

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
        UpdateHistoryLabels();
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
        using var speculativeCts = new CancellationTokenSource();
        Task<AnalyzeResult>? speculative = null;
        try
        {
            // Facebook and X post links are only known to be photo posts after their page is fetched; a
            // video post then falls through to yt-dlp. Start that yt-dlp analysis in parallel so a video
            // post doesn't pay for the page fetch and the extraction one after the other.
            if (FacebookPostService.IsPostUrl(url) || XPostService.IsPostUrl(url))
            {
                cookiesPath = await AppSettingsService.Instance.AcquireCookiesFileAsync();
                speculative = _ytDlp.AnalyzeAsync(url, cookiesPath, speculativeCts.Token);
            }

            // TikTok photo posts (yt-dlp only extracts their audio), Facebook / Instagram photo posts and
            // stories (photos unsupported / login-only in yt-dlp) never reach yt-dlp — they get the
            // pick-and-save gallery instead.
            MediaGalleryPost? galleryPost;
            try
            {
                galleryPost = await TryFetchGalleryPostAsync(url);
            }
            catch
            {
                AbandonSpeculative(speculative, speculativeCts);
                throw;
            }
            if (galleryPost is not null)
            {
                AbandonSpeculative(speculative, speculativeCts);
                ShowGalleryPost(galleryPost);
                return;
            }

            // YouTube never gets cookies (yt-dlp skips them there) — don't decrypt / write cookies.txt for it.
            if (speculative is null && YtDlpService.NeedsCookies(url))
                cookiesPath = await AppSettingsService.Instance.AcquireCookiesFileAsync();

            if (speculative is not null)
            {
                ShowAnalyzeResult(await speculative);
            }
            else if (YtDlpService.LooksLikeSingleVideoUrl(url))
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
                ShowAnalyzeResult(await _ytDlp.AnalyzeAsync(url, cookiesPath));
            }
        }
        catch (Exception ex)
        {
            HidePreviewOnError();
            ErrorBar.Message = ex.Message;
            ErrorBar.Title = L("Home.ErrorTitle");
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(cookiesPath)) AppSettingsService.Instance.ReleaseCookiesFile();
            AnalyzingRing.IsActive = false;
            AnalyzeButton.IsEnabled = true;
            _analyzeInProgress = false;
        }
    }

    private void ShowAnalyzeResult(AnalyzeResult result)
    {
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

    /// <summary>Kills a speculative yt-dlp analysis whose result is no longer needed and observes its outcome.</summary>
    private static void AbandonSpeculative(Task<AnalyzeResult>? speculative, CancellationTokenSource cts)
    {
        if (speculative is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        _ = speculative.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
    }

    // ─── Playlist ─────────────────────────────────────────────────────────────

}

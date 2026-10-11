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

/// <summary>Home page: the YT-DLP downloader (URL analyze, options, playlist, log, history).</summary>
public sealed partial class HomePage : Page
{
    private readonly YtDlpService _ytDlp = new(
        ytDlpPath: ResolveYtDlpPath(),
        ffmpegDirectory: ResolveFfmpegDirectory());
    private readonly HistoryStore _historyStore = new();
    private readonly ObservableCollection<DownloadHistoryItem> _history = new();

    private VideoInfo? _currentVideo;
    private PlaylistInfo? _currentPlaylist;
    private CancellationTokenSource? _downloadCts;
    private ObservableCollection<SubtitleSelection>? _autoSubtitleLangs;

    private bool _analyzeInProgress;   // guards Enter+click / double-click races while an analyze runs
    private bool _previewActive;       // instant card is shown but the full dump hasn't populated options yet
    private int _activeDownloads;      // so an analyze finish never re-enables DownloadNow mid-download

    private readonly TikTokPhotoService _tikTok = new();
    private readonly FacebookStoryService _facebookStories = new();
    private readonly InstagramStoryService _instagramStories = new();
    private readonly FacebookPostService _facebookPosts = new();
    private readonly InstagramPostService _instagramPosts = new();
    private MediaGalleryPost? _currentGalleryPost;
    private CancellationTokenSource? _galleryLoadCts; // cancels thumbnail loading when another post replaces it
    private bool _galleryDownloading;

    /// <summary>
    /// Looks for yt-dlp.exe next to the running app first (so it works after you publish/copy
    /// the app elsewhere), then falls back to the known dev-machine bin folder, then finally
    /// just "yt-dlp.exe" so it still works if it's on PATH.
    /// </summary>
    private static string ResolveYtDlpPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin", "yt-dlp.exe"),
            Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe"),
            @"C:\Users\doubl\Documents\y2akio\y2akio\bin\yt-dlp.exe",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return "yt-dlp.exe"; // last resort: hope it's on PATH
    }

    private static string ResolveFfmpegDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin"),
            Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg-9.0.1-full_build", "bin"),
            @"C:\Users\doubl\Documents\y2akio\y2akio\bin",
            @"C:\Users\doubl\Documents\y2akio\y2akio\bin\ffmpeg-9.0.1-full_build\bin",
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "ffmpeg.exe"))) return dir;
        }

        return ""; // let yt-dlp look on PATH
    }

    public HomePage()
    {
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
        ErrorBar.Title   = L("Home.ErrorTitle");
        SuccessBar.Title = L("Home.SuccessTitle");

        // Download options section
        DownloadOptionsTitle.Text  = L("Home.DownloadOptions");
        FileFormatLabel.Text       = L("Home.FileFormat");
        VideoFormatToggle.Content  = L("Home.VideoMp4");
        AudioFormatToggle.Content  = L("Home.AudioMusic");
        QualityLabel.Text          = L("Home.Quality");
        VideoFormatLabel.Text      = L("Home.VideoFormat");
        AudioFormatLabel.Text      = L("Home.AudioFormat");

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
        FileNamePrefixLabel.Text   = L("Home.FileNamePrefix");
        PrefixDateTimeCheck.Content = L("Home.PrefixDateTime");
        PrefixChannelCheck.Content  = L("Home.PrefixChannel");
        IncludeTitleCheck.Content   = L("Home.IncludeTitle");

        // Subtitles
        SubtitlesCheck.Content       = L("Home.DownloadSubtitles");
        SubtitleOptionsTitle.Text    = L("Home.SubtitleOptions");
        SubtitleEmbedRadio.Content   = L("Home.SubtitleEmbed");
        SubtitleSeparateRadio.Content= L("Home.SubtitleSeparate");
        IncludeTracksLabel.Text      = L("Home.IncludeTracks");
        SubtitleManualCheck.Content  = L("Home.ManualSubs");
        SubtitleAutoCheck.Content    = L("Home.AutoSubs");
        AutoLanguagesLabel.Text      = L("Home.Languages");
        ManualSelectAllButton.Content  = L("Home.SelectAll");
        ManualDeselectAllButton.Content= L("Home.DeselectAll");
        AutoSelectAllButton.Content    = L("Home.SelectAll");
        AutoDeselectAllButton.Content  = L("Home.DeselectAll");
        SubtitleFormatLabel.Text       = L("Home.SubtitleFormat");

        // Save locations
        VideoSaveLocationLabel.Text    = L("Home.VideoSaveLocation");
        AudioSaveLocationLabel.Text    = L("Home.AudioSaveLocation");
        ChooseVideoFolderButton.Content = L("Home.ChooseFolder");
        ChooseAudioFolderButton.Content = L("Home.ChooseFolder");

        // Extra options
        ExtraOptionsLabel.Text       = L("Home.ExtraOptions");
        EmbedMetadataCheck.Content   = L("Home.EmbedMetadata");
        ThumbnailLabel.Text          = L("Home.Thumbnail");

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
        DownloadHistoryTitle.Text  = L("Home.DownloadHistory");
        ClearHistoryButton.Content = L("Home.ClearAll");

        // Playlist button
        UpdateDownloadPlaylistButton();

        // TikTok photo gallery
        GallerySelectAllButton.Content    = L("Home.SelectAll");
        GalleryDeselectAllButton.Content  = L("Home.DeselectAll");
        GalleryHint.Text           = L("Home.GalleryHint");
        ToolTipService.SetToolTip(ChooseGalleryFolderButton, L("Home.GallerySaveLocation"));
        ToolTipService.SetToolTip(GallerySaveModeComboBox, L("Home.GallerySaveAs"));
        GallerySaveFolderItem.Content     = L("Home.GallerySaveFolder");
        GallerySaveZipItem.Content        = L("Home.GallerySaveZip");
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

    private void PopulatePlaylistPanel(PlaylistInfo playlist)
    {
        PlaylistTitleText.Text = playlist.Title;
        var meta = new List<string> { $"{playlist.Count} videos" };
        if (!string.IsNullOrWhiteSpace(playlist.Uploader)) meta.Add(playlist.Uploader);
        PlaylistMetaText.Text = string.Join(" · ", meta);

        PlaylistListView.ItemsSource = playlist.Entries;
        UpdateDownloadPlaylistButton();
    }

    private void PlaylistEntryCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateDownloadPlaylistButton();

    private void UpdateDownloadPlaylistButton()
    {
        var selected = _currentPlaylist?.Entries.Count(e => e.Selected) ?? 0;
        var baseText = L("Home.DownloadSelected");
        DownloadPlaylistButton.Content = selected > 0 ? $"{baseText} ({selected})" : baseText;
        DownloadPlaylistButton.IsEnabled = selected > 0;
    }

    private async void PlaylistListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaylistEntry entry || string.IsNullOrWhiteSpace(entry.Url))
            return;
        if (_analyzeInProgress) return;
        _analyzeInProgress = true;

        ErrorBar.IsOpen = false;
        AnalyzingRing.IsActive = true;
        DownloadPlaylistButton.IsEnabled = false;

        // Paint the options card instantly from the track's own metadata (title/thumb/duration are already
        // in hand at zero cost), then one full dump fills quality/res/subtitle options.
        ShowInstantCard(entry.Title, "", entry.DurationDisplay, ResolveEntryThumbnail(entry));

        string cookiesPath = "";
        try
        {
            cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();
            var video = await _ytDlp.AnalyzeVideoAsync(entry.Url, cookiesPath);
            FinishVideoOptions(video);
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
            DownloadPlaylistButton.IsEnabled = true;
            _analyzeInProgress = false;
        }
    }

    private async void DownloadPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is null) return;

        var selected = _currentPlaylist.Entries.Where(en => en.Selected).ToList();
        if (selected.Count == 0) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";

        _activeDownloads++;
        DownloadPlaylistButton.IsEnabled = false;
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

        string cookiesPath = "";
        try
        {
            cookiesPath = await AppSettingsService.Instance.GetCookiesFilePathAsync();
            var completed = 0;
            foreach (var entry in selected)
            {
                completed++;
                DownloadStatusText.Text = $"({completed}/{selected.Count}) Downloading: {entry.Title}";
                DownloadProgressBar.Value = 0;
                AppendLog($"=== ({completed}/{selected.Count}) {entry.Title} ===");

                var request = BuildDownloadRequest(entry.Url, isVideo, qualitySelection, cookiesPath);
                var savedPath = await _ytDlp.DownloadAsync(request, progress, _downloadCts.Token, AppendLog);
                AppendLog($"Saved: {savedPath}");

                _history.Insert(0, new DownloadHistoryItem
                {
                    Title = entry.Title,
                    Kind = request.Kind,
                    QualityLabel = qualitySelection,
                    FilePath = savedPath,
                    FolderPath = request.SaveFolder,
                    CompletedAt = DateTime.Now,
                });
            }
            await _historyStore.SaveAsync(_history);
            ToastService.ShowDownloadComplete($"{selected.Count} videos downloaded", _currentPlaylist.Title);
            ShowDownloadComplete($"{selected.Count} videos downloaded from playlist");
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
            DownloadPlaylistButton.IsEnabled = true;
        }
    }

    // ─── Gallery posts (TikTok photo posts, Facebook / Instagram posts + stories) ─

    /// <summary>
    /// Fetches the link as a gallery post when it is one (TikTok photo post, Facebook / Instagram post
    /// or story), else returns null so the normal yt-dlp flow handles it. TikTok share short links
    /// (vt./vm.tiktok.com) only reveal whether they point at a photo post after their redirect resolves.
    /// </summary>
    private async Task<MediaGalleryPost?> TryFetchGalleryPostAsync(string url)
    {
        Func<string?, Task<MediaGalleryPost?>>? fetch = null;
        if (FacebookStoryService.IsStoryUrl(url)) fetch = async c => await _facebookStories.FetchStoryAsync(url, c);
        else if (FacebookPostService.IsPostUrl(url)) fetch = c => _facebookPosts.FetchPostAsync(url, c); // null = video post → yt-dlp
        else if (InstagramStoryService.IsStoryUrl(url)) fetch = async c => await _instagramStories.FetchStoryAsync(url, c);
        else if (InstagramPostService.IsPostUrl(url)) fetch = async c => await _instagramPosts.FetchPostAsync(url, c);
        else if (TikTokPhotoService.IsPhotoUrl(url)) fetch = async c => await _tikTok.FetchPostAsync(url, c);
        else if (TikTokPhotoService.IsShortLink(url))
        {
            var resolved = await _tikTok.ResolveShortLinkAsync(url);
            if (TikTokPhotoService.IsPhotoUrl(resolved)) fetch = async c => await _tikTok.FetchPostAsync(resolved, c);
        }
        if (fetch is null) return null;

        if (_galleryDownloading)
            throw new InvalidOperationException(L("Home.GalleryBusy"));

        // Cookies are decrypted in memory only — this path never needs the plaintext cookies.txt on disk.
        return await fetch(await AppSettingsService.Instance.DecryptCookiesAsync());
    }

    private void ShowGalleryPost(MediaGalleryPost post)
    {
        _galleryLoadCts?.Cancel();
        _galleryLoadCts = new CancellationTokenSource();

        _currentGalleryPost = post;
        GalleryTitleText.Text = !string.IsNullOrWhiteSpace(post.Title) ? post.Title.Trim()
            : !string.IsNullOrWhiteSpace(post.Nickname) ? post.Nickname
            : $"@{post.Author}";

        var meta = new List<string> { post.Source };
        var photos = post.Items.Count(i => !i.IsVideo);
        var videos = post.Items.Count - photos;
        if (photos > 0) meta.Add(string.Format(L("Home.GalleryPhotoCount"), photos));
        if (videos > 0) meta.Add(string.Format(L("Home.GalleryVideoCount"), videos));
        meta.Add($"@{post.Author}");
        if (!string.IsNullOrWhiteSpace(post.Nickname) && post.Nickname != GalleryTitleText.Text) meta.Add(post.Nickname);
        GalleryMetaText.Text = string.Join(" · ", meta);

        if (string.IsNullOrWhiteSpace(GallerySaveLocationText.Text))
            GallerySaveLocationText.Text = AppSettingsService.Instance.VideoSaveFolder;

        GalleryGrid.ItemsSource = post.Items;
        GalleryProgressPanel.Visibility = Visibility.Collapsed;
        UpdateGalleryButtons();
        GalleryPanel.Visibility = Visibility.Visible;

        _ = LoadGalleryThumbnailsAsync(post, _galleryLoadCts.Token);
    }

    /// <summary>
    /// Paints every tile (4 fetches at a time). Photos fetch their full-size bytes once and keep them on
    /// the item, so saving later is instant and never hits the CDN twice; videos (and lazily-resolved
    /// photos) only fetch their thumbnail here. Runs on the UI context, so the item property updates are safe.
    /// </summary>
    private async Task LoadGalleryThumbnailsAsync(MediaGalleryPost post, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = post.Items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                byte[] tileBytes;
                if (item.ThumbnailUrls.Count > 0)
                {
                    tileBytes = await MediaGalleryService.DownloadAsync(item.ThumbnailUrls, post.Referer, ct);
                }
                else if (item.IsVideo)
                {
                    item.Failed = true;
                    return;
                }
                else
                {
                    tileBytes = await MediaGalleryService.GetItemBytesAsync(item, post.Referer, ct);
                }
                item.Thumbnail = await CreateBitmapAsync(tileBytes, decodeWidth: 344);
            }
            catch (OperationCanceledException) { /* a newer analyze replaced this post */ }
            catch
            {
                item.Failed = true; // HEIC without the codec, or the CDN refused — the save step retries the fetch
            }
            finally
            {
                gate.Release();
            }
        });

        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* ignore */ }
    }

    private static async Task<BitmapImage> CreateBitmapAsync(byte[] bytes, int decodeWidth = 0)
    {
        var bitmap = new BitmapImage();
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth; // keep gallery tiles light on memory
        using var stream = new MemoryStream(bytes).AsRandomAccessStream();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void GalleryGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateGalleryButtons();

    private void GallerySelectAllButton_Click(object sender, RoutedEventArgs e) => GalleryGrid.SelectAll();

    private void GalleryDeselectAllButton_Click(object sender, RoutedEventArgs e) => GalleryGrid.SelectedItems.Clear();

    private void UpdateGalleryButtons()
    {
        var total = _currentGalleryPost?.Items.Count ?? 0;
        var selected = GalleryGrid.SelectedItems.Count;

        var selectedText = L("Home.DownloadSelected");
        GalleryDownloadSelectedButton.Content = selected > 0 ? $"{selectedText} ({selected})" : selectedText;
        GalleryDownloadAllButton.Content = $"{L("Home.GalleryDownloadAll")} ({total})";

        GalleryDownloadSelectedButton.IsEnabled = selected > 0 && !_galleryDownloading;
        GalleryDownloadAllButton.IsEnabled = total > 0 && !_galleryDownloading;
    }

    /// <summary>Double-click a tile: show the photo full size, or play the video, in a dialog.</summary>
    private async void GalleryGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not MediaGalleryItem item) return;

        UIElement content;
        MediaPlayerElement? player = null;
        if (item.IsVideo)
        {
            if (item.Urls.Count == 0 || !Uri.TryCreate(item.Urls[0], UriKind.Absolute, out var videoUri)) return;
            player = new MediaPlayerElement
            {
                Source = Windows.Media.Core.MediaSource.CreateFromUri(videoUri),
                AreTransportControlsEnabled = true,
                AutoPlay = true,
                MaxHeight = 620,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            };
            content = player;
        }
        else
        {
            BitmapImage bitmap;
            try { bitmap = await CreateBitmapAsync(await MediaGalleryService.GetItemBytesAsync(item, _currentGalleryPost?.Referer)); }
            catch { return; }

            content = new ScrollViewer
            {
                MaxHeight = 640,
                ZoomMode = ZoomMode.Enabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Image { Source = bitmap, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, MaxHeight = 620 },
            };
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{item.Index} / {_currentGalleryPost?.Items.Count ?? 0}",
            CloseButtonText = L("Settings.Ok"),
            Content = content,
        };
        await dialog.ShowAsync();

        // Stop playback/buffering once the dialog is gone.
        if (player is not null)
        {
            player.MediaPlayer?.Pause();
            (player.Source as IDisposable)?.Dispose();
            player.Source = null;
        }
    }

    private async void ChooseGalleryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
            GallerySaveLocationText.Text = folder.Path;
    }

    private void GalleryDownloadSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        // Keep post order regardless of the order the user clicked tiles in.
        var selected = GalleryGrid.SelectedItems.OfType<MediaGalleryItem>().OrderBy(i => i.Index).ToList();
        _ = SaveGalleryItemsAsync(selected);
    }

    private void GalleryDownloadAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentGalleryPost is { } post)
            _ = SaveGalleryItemsAsync(post.Items);
    }

    /// <summary>
    /// Saves the given items as loose files in a per-post subfolder (<c>author_postId\</c>) or as one
    /// <c>author_postId.zip</c>, depending on the "Save as" choice. Videos, and photos the gallery
    /// couldn't fetch yet, are downloaded first.
    /// </summary>
    private async Task SaveGalleryItemsAsync(IReadOnlyList<MediaGalleryItem> items)
    {
        if (_currentGalleryPost is not { } post || items.Count == 0 || _galleryDownloading) return;

        var saveFolder = GallerySaveLocationText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(saveFolder)) saveFolder = AppSettingsService.Instance.VideoSaveFolder;
        var asZip = GallerySaveModeComboBox.SelectedIndex == 1;

        _galleryDownloading = true;
        UpdateGalleryButtons();
        ErrorBar.IsOpen = false;
        SuccessBar.IsOpen = false;
        GalleryProgressBar.Value = 0;
        GalleryProgressPanel.Visibility = Visibility.Visible;
        ShowLogPanel();
        AppendLog($"{post.Source} {post.Id} by @{post.Author}: saving {items.Count} item(s){(asZip ? " as ZIP" : "")}");

        try
        {
            var payloads = new List<(MediaGalleryItem Item, byte[] Data)>();
            foreach (var item in items)
            {
                GalleryStatusText.Text = string.Format(L("Home.GalleryFetching"), payloads.Count + 1, items.Count);
                payloads.Add((item, await MediaGalleryService.GetItemBytesAsync(item, post.Referer)));
                GalleryProgressBar.Value = payloads.Count * 90.0 / items.Count; // last 10% = writing to disk
            }

            GalleryStatusText.Text = L("Home.GalleryWriting");
            var baseName = MediaGalleryService.BaseNameFor(post);
            string resultPath;
            string folderPath;
            if (asZip)
            {
                resultPath = await MediaGalleryService.SaveAsZipAsync(post, payloads, Path.Combine(saveFolder, baseName + ".zip"));
                folderPath = saveFolder;
            }
            else
            {
                folderPath = Path.Combine(saveFolder, baseName);
                var written = await MediaGalleryService.SaveToFolderAsync(post, payloads, folderPath);
                resultPath = written[0];
            }

            GalleryProgressBar.Value = 100;
            GalleryStatusText.Text = L("Home.GalleryDone");
            var savedTarget = asZip ? resultPath : folderPath;
            AppendLog($"Saved: {savedTarget}");

            var title = !string.IsNullOrWhiteSpace(post.Title) ? post.Title.Trim() : $"@{post.Author} · {post.Id}";
            ToastService.ShowDownloadComplete(title, resultPath);
            ShowDownloadComplete(string.Format(L("Home.GallerySaved"), items.Count, savedTarget));

            _history.Insert(0, new DownloadHistoryItem
            {
                Title = title,
                Kind = items.All(i => i.IsVideo) ? MediaKind.Video : MediaKind.Image,
                QualityLabel = $"{post.Source} · {(asZip ? "ZIP · " : "")}{items.Count} file(s)",
                FilePath = resultPath,
                FolderPath = folderPath,
                CompletedAt = DateTime.Now,
            });
            await _historyStore.SaveAsync(_history);
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            GalleryStatusText.Text = ex.Message;
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _galleryDownloading = false;
            UpdateGalleryButtons();
        }
    }

    // ─── Log ─────────────────────────────────────────────────────────────────

    private void ShowLogPanel()
    {
        LogTextBlock.Text = "";
        LogPanel.Visibility = Visibility.Visible;
    }

    private void AppendLog(string line)
    {
        // onLogLine is raised on yt-dlp's process thread; UI must be touched on the dispatcher.
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}\n";
        DispatcherQueue.TryEnqueue(() =>
        {
            LogTextBlock.Text += text;
            // Keep the newest lines in view.
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        });
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBlock.Text = "";
    }

    private void ShowDownloadComplete(string message)
    {
        SuccessBar.Message = message;
        SuccessBar.IsOpen = true;
    }

    // ─── Options panel ────────────────────────────────────────────────────────

    /// <summary>Resets every options control so a fresh analyze never shows stale state from the previous video.</summary>
    private void ClearOptionsState()
    {
        _currentVideo = null;
        QualityComboBox.Items.Clear();
        SubtitleLanguageComboBox.Items.Clear();
        _autoSubtitleLangs = null;
        AutoLanguageList.ItemsSource = null;
        DownloadNowButton.IsEnabled = false;
    }

    /// <summary>
    /// Shows the options panel immediately with whatever title/thumb is already in hand (oEmbed for a typed
    /// URL, playlist-entry data for a track click). Quality/subtitle controls stay empty and Download is
    /// disabled until the full dump lands and <see cref="FinishVideoOptions"/> fills them.
    /// </summary>
    private void ShowInstantCard(string title, string uploader, string durationDisplay, string? thumbUrl)
    {
        ClearOptionsState();
        _previewActive = true;

        VideoTitleText.Text = title;
        UploaderText.Text = uploader;
        DurationText.Text = durationDisplay;
        if (!string.IsNullOrWhiteSpace(thumbUrl) && Uri.TryCreate(thumbUrl, UriKind.Absolute, out var thumbUri))
            ThumbnailImage.Source = new BitmapImage(thumbUri);

        OptionsPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Full dump arrived: swap the instant card for the complete, populated options panel.</summary>
    private void FinishVideoOptions(VideoInfo video)
    {
        _currentVideo = video;
        PopulateOptionsPanel(video); // overwrites the card + fills subtitle languages + quality list
        _previewActive = false;
        OptionsPanel.Visibility = Visibility.Visible;
        DownloadNowButton.IsEnabled = _activeDownloads == 0;
    }

    /// <summary>An analyze failed while only the instant card was showing — hide it rather than leave a half-loaded panel.</summary>
    private void HidePreviewOnError()
    {
        if (!_previewActive) return;
        OptionsPanel.Visibility = Visibility.Collapsed;
        _previewActive = false;
        DownloadNowButton.IsEnabled = _activeDownloads == 0;
    }

    /// <summary>
    /// Fire-and-forget: paint the card from YouTube oEmbed (~0.2s) while the full dump is still running.
    /// No-ops if the preview already ended (dump beat it) or oEmbed returned nothing. Continuation must
    /// resume on the UI thread, so no ConfigureAwait(false) here.
    /// </summary>
    private async Task TryPaintYouTubePreviewAsync(string url)
    {
        try
        {
            var preview = await _ytDlp.FetchYouTubePreviewAsync(url);
            if (preview is not null && _previewActive)
                ShowInstantCard(preview.Title, preview.Author, "", preview.ThumbnailUrl);
        }
        catch
        {
            // best effort — the full dump paints the card anyway
        }
    }

    /// <summary>Playlist flat entries often omit the thumbnail; derive the standard YouTube one from the id.</summary>
    private static string? ResolveEntryThumbnail(PlaylistEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl)) return entry.ThumbnailUrl;
        if (!string.IsNullOrWhiteSpace(entry.Id)) return $"https://i.ytimg.com/vi/{entry.Id}/hqdefault.jpg";
        return null;
    }

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
        var isVideo = sender == VideoFormatToggle;
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

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        await _historyStore.SaveAsync(_history);
    }

    private void PlayHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item && File.Exists(item.FilePath))
        {
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(item.FilePath));
        }
    }

    private async void CopyPathHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            var package = new DataPackage();
            package.SetText(item.FilePath);
            Clipboard.SetContent(package);
        }
        await Task.CompletedTask;
    }

    private void OpenFolderHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item && Directory.Exists(item.FolderPath))
        {
            _ = Windows.System.Launcher.LaunchFolderPathAsync(item.FolderPath);
        }
    }

    private async void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DownloadHistoryItem item)
        {
            _history.Remove(item);
            await _historyStore.SaveAsync(_history);
        }
    }
}

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
using y2akio.Models;
using y2akio.Services;

namespace y2akio;

public sealed partial class MainWindow : Window
{
    private readonly YtDlpService _ytDlp = new(
        ytDlpPath: ResolveYtDlpPath(),
        ffmpegDirectory: ResolveFfmpegDirectory());
    private readonly HistoryStore _historyStore = new();
    private readonly ObservableCollection<DownloadHistoryItem> _history = new();

    private VideoInfo? _currentVideo;
    private PlaylistInfo? _currentPlaylist;
    private CancellationTokenSource? _downloadCts;

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

    public MainWindow()
    {
        InitializeComponent();

        Title = "YT-DLP Media Downloader";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        HistoryList.ItemsSource = _history;
        SaveLocationTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var items = await _historyStore.LoadAsync();
        foreach (var item in items.OrderByDescending(i => i.CompletedAt))
            _history.Add(item);
    }

    private void UrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            _ = AnalyzeAsync();
    }

    private void AnalyzeButton_Click(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

    private async Task AnalyzeAsync()
    {
        var url = UrlTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(url))
            return;

        ErrorBar.IsOpen = false;
        OptionsPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        AnalyzingRing.IsActive = true;
        AnalyzeButton.IsEnabled = false;

        try
        {
            var result = await _ytDlp.AnalyzeAsync(url);

            if (result.IsPlaylist && result.Playlist is { } playlist)
            {
                _currentPlaylist = playlist;
                PopulatePlaylistPanel(playlist);
                PlaylistPanel.Visibility = Visibility.Visible;
            }
            else if (result.Video is { } video)
            {
                _currentVideo = video;
                PopulateOptionsPanel(video);
                OptionsPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            AnalyzingRing.IsActive = false;
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void PopulatePlaylistPanel(PlaylistInfo playlist)
    {
        PlaylistTitleText.Text = playlist.Title;
        var meta = new System.Collections.Generic.List<string> { $"{playlist.Count} videos" };
        if (!string.IsNullOrWhiteSpace(playlist.Uploader)) meta.Add(playlist.Uploader);
        PlaylistMetaText.Text = string.Join(" · ", meta);

        PlaylistListView.ItemsSource = playlist.Entries;
        UpdateDownloadPlaylistButton();
    }

    private void PlaylistEntryCheckBox_Changed(object sender, RoutedEventArgs e) => UpdateDownloadPlaylistButton();

    private void UpdateDownloadPlaylistButton()
    {
        var selected = _currentPlaylist?.Entries.Count(e => e.Selected) ?? 0;
        DownloadPlaylistButton.Content = selected > 0 ? $"Download Selected ({selected})" : "Download Selected";
        DownloadPlaylistButton.IsEnabled = selected > 0;
    }

    private async void PlaylistListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PlaylistEntry entry || string.IsNullOrWhiteSpace(entry.Url))
            return;

        ErrorBar.IsOpen = false;
        AnalyzingRing.IsActive = true;
        DownloadPlaylistButton.IsEnabled = false;

        try
        {
            var video = await _ytDlp.AnalyzeVideoAsync(entry.Url);
            _currentVideo = video;
            PopulateOptionsPanel(video);
            OptionsPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            AnalyzingRing.IsActive = false;
            DownloadPlaylistButton.IsEnabled = true;
        }
    }

    private async void DownloadPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist is null) return;

        var selected = _currentPlaylist.Entries.Where(en => en.Selected).ToList();
        if (selected.Count == 0) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";

        DownloadPlaylistButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadStatusText.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.Percent;
            DownloadStatusText.Text = p.StatusText;
        });

        try
        {
            var completed = 0;
            foreach (var entry in selected)
            {
                completed++;
                DownloadStatusText.Text = $"({completed}/{selected.Count}) Downloading: {entry.Title}";
                DownloadProgressBar.Value = 0;

                var request = BuildDownloadRequest(entry.Url, isVideo, qualitySelection);
                var savedPath = await _ytDlp.DownloadAsync(request, progress, _downloadCts.Token);

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
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            DownloadPlaylistButton.IsEnabled = true;
        }
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
        SubtitleLanguageComboBox.Items.Clear();
        SubtitleLanguageComboBox.Items.Add(new SubtitleLanguage("all", "All languages (auto)"));

        // Prefer author-uploaded names; fall back to auto-generated ones for languages only auto has.
        var combined = new Dictionary<string, string>();
        foreach (var (code, name) in video.ManualSubtitleLanguages) combined[code] = name;
        foreach (var (code, name) in video.AutoSubtitleLanguages)
            combined.TryAdd(code, name);

        foreach (var (code, name) in combined.OrderBy(p => p.Value))
            SubtitleLanguageComboBox.Items.Add(new SubtitleLanguage(code, $"{name} ({code})"));
        SubtitleLanguageComboBox.SelectedIndex = 0;
    }

    private void SubtitlesCheck_Toggled(object sender, RoutedEventArgs e)
    {
        SubtitleOptionsPanel.Visibility = SubtitlesCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SubtitleSource_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before all fields are wired; skip until layout done.
        if (SubtitleLanguageComboBox is null) return;
        // No sources selected = nothing to download; mirror yt-dlp guard.
        SubtitleLanguageComboBox.IsEnabled = SubtitleManualCheck.IsChecked == true || SubtitleAutoCheck.IsChecked == true;
    }

    private void FileFormatToggle_Click(object sender, RoutedEventArgs e)
    {
        var isVideo = sender == VideoFormatToggle;
        VideoFormatToggle.IsChecked = isVideo;
        AudioFormatToggle.IsChecked = !isVideo;
        ContainerFormatPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        RefreshQualityOptions();
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
            foreach (var kbps in new[] { 320, 256, 192, 128 })
            {
                var qualityLabel = kbps >= 256 ? "High Quality" : "Standard";
                QualityComboBox.Items.Add($"MP3 {kbps} kbps ({qualityLabel})");
            }
        }

        QualityComboBox.SelectedIndex = 0;
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
            SaveLocationTextBox.Text = folder.Path;
    }

    private DownloadRequest BuildDownloadRequest(string url, bool isVideo, string qualitySelection)
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

        return new DownloadRequest
        {
            Url = url,
            Kind = isVideo ? MediaKind.Video : MediaKind.Audio,
            SaveFolder = SaveLocationTextBox.Text,
            TargetHeight = targetHeight,
            ContainerFormat = (ContainerComboBox.SelectedItem as string ?? "MP4").ToLowerInvariant(),
            AudioBitrateKbps = audioKbps,
            PrefixDateTime = PrefixDateTimeCheck.IsChecked == true,
            PrefixChannel = PrefixChannelCheck.IsChecked == true,
            IncludeTitle = IncludeTitleCheck.IsChecked == true,
            SubtitlesEnabled = SubtitlesCheck.IsChecked == true,
            SubtitleManual = SubtitleManualCheck.IsChecked == true,
            SubtitleAuto = SubtitleAutoCheck.IsChecked == true,
            SubtitleLangCode = SubtitleLanguageComboBox.SelectedItem is SubtitleLanguage lang ? lang.Code : "all",
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

    private async void DownloadNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";
        var request = BuildDownloadRequest(_currentVideo.OriginalUrl, isVideo, qualitySelection);

        DownloadNowButton.IsEnabled = false;
        DownloadProgressBar.Visibility = Visibility.Visible;
        DownloadStatusText.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.Percent;
            DownloadStatusText.Text = p.StatusText;
        });

        try
        {
            var savedPath = await _ytDlp.DownloadAsync(request, progress, _downloadCts.Token);

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
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            DownloadNowButton.IsEnabled = true;
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Hook this up to a settings dialog for yt-dlp.exe path, ffmpeg path, default save folder, etc.
    }

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
using System;
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
        AnalyzingRing.IsActive = true;
        AnalyzeButton.IsEnabled = false;

        try
        {
            var video = await _ytDlp.AnalyzeAsync(url);
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
            AnalyzeButton.IsEnabled = true;
        }
    }

    private void PopulateOptionsPanel(VideoInfo video)
    {
        VideoTitleText.Text = video.Title;
        UploaderText.Text = video.Uploader;
        DurationText.Text = video.DurationDisplay;

        if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl) && Uri.TryCreate(video.ThumbnailUrl, UriKind.Absolute, out var thumbUri))
            ThumbnailImage.Source = new BitmapImage(thumbUri);

        RefreshQualityOptions();
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

    private async void DownloadNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentVideo is null) return;

        var isVideo = VideoFormatToggle.IsChecked == true;
        int? targetHeight = null;
        int? audioKbps = null;

        var qualitySelection = QualityComboBox.SelectedItem as string ?? "";
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

        var request = new DownloadRequest
        {
            Url = _currentVideo.OriginalUrl,
            Kind = isVideo ? MediaKind.Video : MediaKind.Audio,
            SaveFolder = SaveLocationTextBox.Text,
            TargetHeight = targetHeight,
            ContainerFormat = (ContainerComboBox.SelectedItem as string ?? "MP4").ToLowerInvariant(),
            AudioBitrateKbps = audioKbps,
            PrefixDateTime = PrefixDateTimeCheck.IsChecked == true,
            PrefixChannel = PrefixChannelCheck.IsChecked == true,
            IncludeTitle = IncludeTitleCheck.IsChecked == true,
            DownloadSubtitles = SubtitlesCheck.IsChecked == true,
            EmbedMetadata = EmbedMetadataCheck.IsChecked == true,
            ThumbnailMode = ThumbnailComboBox.SelectedIndex switch
            {
                0 => "embed",
                1 => "save",
                _ => "none",
            },
        };

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
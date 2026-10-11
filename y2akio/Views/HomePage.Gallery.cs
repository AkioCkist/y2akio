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

/// <summary>Social gallery discovery, previews, selection, and saving.</summary>
public sealed partial class HomePage : Page
{
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

}

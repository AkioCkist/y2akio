using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace y2akio.Models;

/// <summary>
/// A post whose media is picked from a gallery instead of handed to yt-dlp: a TikTok photo post
/// (slideshow) or a Facebook story bucket. Items keep their on-site order.
/// </summary>
public class MediaGalleryPost
{
    public string Source { get; init; } = "";      // "TikTok" / "Facebook Story" — shown in the header
    public string Id { get; init; } = "";
    public string Author { get; init; } = "";      // handle / slug, used in file names
    public string Nickname { get; init; } = "";    // display name
    public string Title { get; init; } = "";
    public List<MediaGalleryItem> Items { get; init; } = new();
    public string? Referer { get; init; }          // sent with media downloads when the CDN wants one

    public bool AllVideos => Items.Count > 0 && Items.All(i => i.IsVideo);
}

/// <summary>
/// One photo or video of a gallery post. Photo bytes are fetched once (for the tile) and reused when
/// saving; videos only fetch their thumbnail up front and download the file at save time.
/// </summary>
public class MediaGalleryItem : INotifyPropertyChanged
{
    public int Index { get; init; }                           // 1-based position in the post
    public bool IsVideo { get; init; }
    public List<string> Urls { get; init; } = new();          // full-size media (CDN mirrors / quality fallbacks), best first
    public List<string> ThumbnailUrls { get; init; } = new(); // tile image for videos (and photos whose Urls are resolved lazily)

    /// <summary>
    /// Optional: looks up the full-size URLs on demand, for photos the page only listed as a preview
    /// (Facebook albums beyond the first few). Run once via <see cref="Services.MediaGalleryService.EnsureFullUrlsAsync"/>;
    /// <see cref="Urls"/> then starts with the full-size result, the preview staying as fallback.
    /// </summary>
    public Func<CancellationToken, Task<List<string>>>? ResolveFullUrlsAsync { get; set; }
    public int Width { get; init; }
    public int Height { get; init; }

    private byte[]? _bytes;
    /// <summary>Cached full-size photo bytes (never set for videos).</summary>
    public byte[]? Bytes
    {
        get => _bytes;
        set { _bytes = value; OnPropertyChanged(); }
    }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingVisibility)); }
    }

    private bool _failed;
    public bool Failed
    {
        get => _failed;
        set { _failed = value; OnPropertyChanged(); OnPropertyChanged(nameof(LoadingVisibility)); OnPropertyChanged(nameof(FailedVisibility)); }
    }

    public Visibility LoadingVisibility => _thumbnail is null && !_failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FailedVisibility => _failed ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VideoBadgeVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public string IndexDisplay => Index.ToString();
    public string SizeDisplay => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";
    public Visibility SizeVisibility => SizeDisplay.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>File extension for a downloaded payload — sniffed from the bytes (sites serve JPEG, sometimes WebP/PNG/HEIC).</summary>
    public string ExtensionFor(byte[] b)
    {
        if (IsVideo) return "mp4";
        if (b.Length >= 12)
        {
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "webp";
            if (b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70) return "heic";
        }
        return "jpg";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

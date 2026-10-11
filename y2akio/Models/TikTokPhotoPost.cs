using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace y2akio.Models;

/// <summary>A TikTok photo post (slideshow): author, caption, and its images in order.</summary>
public class TikTokPhotoPost
{
    public string Id { get; init; } = "";
    public string Author { get; init; } = "";      // @handle without the "@"
    public string Nickname { get; init; } = "";
    public string Title { get; init; } = "";       // photo title, or the post caption when there is none
    public List<TikTokPhotoItem> Images { get; init; } = new();
}

/// <summary>One image of a photo post. Bytes are fetched once (for the gallery) and reused when saving.</summary>
public class TikTokPhotoItem : INotifyPropertyChanged
{
    public int Index { get; init; }                 // 1-based position in the post
    public List<string> Urls { get; init; } = new(); // CDN mirrors, JPEG first
    public int Width { get; init; }
    public int Height { get; init; }

    private byte[]? _bytes;
    public byte[]? Bytes
    {
        get => _bytes;
        set { _bytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(Extension)); }
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

    public string IndexDisplay => Index.ToString();
    public string SizeDisplay => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "";

    /// <summary>File extension sniffed from the downloaded bytes (TikTok serves JPEG, occasionally WebP/PNG/HEIC).</summary>
    public string Extension
    {
        get
        {
            var b = _bytes;
            if (b is { Length: >= 12 })
            {
                if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
                if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
                    && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "webp";
                if (b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70) return "heic";
            }
            return "jpg";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

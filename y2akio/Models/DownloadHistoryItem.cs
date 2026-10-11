using System;

namespace y2akio.Models;

public enum MediaKind { Video, Audio, Image }

/// <summary>One row in the "Download History" list.</summary>
public class DownloadHistoryItem
{
    public string Title { get; set; } = "";
    public MediaKind Kind { get; set; }
    public string QualityLabel { get; set; } = "";     // "Best Quality", "MP3 320 kbps", etc.
    public string FilePath { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public DateTime CompletedAt { get; set; } = DateTime.Now;

    // Convenience strings for binding directly in XAML
    public string KindGlyph => Kind switch // Segoe Fluent Icons: video / music note / photo
    {
        MediaKind.Video => "\uE714",
        MediaKind.Audio => "\uE8D6",
        _               => "\uEB9F",
    };
    public string Subtitle => $"{Kind}· {QualityLabel} · Saved in: {FolderPath} · {CompletedAt:HH:mm:ss d/M/yyyy}";
}

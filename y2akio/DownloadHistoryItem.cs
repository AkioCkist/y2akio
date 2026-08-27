using System;

namespace y2akio.Models;

public enum MediaKind { Video, Audio }

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
    public string KindGlyph => Kind == MediaKind.Video ? "\uE714" : "\uE8D6"; // Segoe Fluent Icons: video / music note
    public string Subtitle => $"{(Kind == MediaKind.Video ? "Video" : "Audio")} · {QualityLabel} · Saved in: {FolderPath} · {CompletedAt:HH:mm:ss d/M/yyyy}";
}

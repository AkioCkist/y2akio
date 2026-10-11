using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>Options collected from the "Download Options" panel.</summary>
public class DownloadRequest
{
    public required string Url { get; init; }
    public required MediaKind Kind { get; init; }
    public required string SaveFolder { get; init; }

    // Video-specific
    public int? TargetHeight { get; init; }        // null = "Best Quality"
    public string ContainerFormat { get; init; } = "mp4"; // mp4 / mkv / webm
    public string VideoCodec { get; init; } = "auto";    // "auto" / "h264" / "hevc" / "av1" / "vp9" / "mpeg4" — non-auto forces a transcode

    // Audio-specific
    public int? AudioBitrateKbps { get; init; }     // 320/256/192/128, null = best
    public string AudioFormat { get; init; } = "mp3"; // mp3/m4a/aac/flac/opus/vorbis/wav/alac/wma

    // Filename prefix toggles
    public bool PrefixDateTime { get; init; }
    public bool PrefixChannel { get; init; }
    public bool IncludeTitle { get; init; } = true;

    // Subtitles (video only — the audio flow never sets these)
    public bool SubtitlesEnabled { get; init; }
    public bool SubtitleManual { get; init; } = true;   // author-uploaded
    public bool SubtitleAuto { get; init; }              // auto-generated
    public string SubtitleLangCode { get; init; } = "all"; // "all" or a specific code like "en"
    public string SubtitleFormat { get; init; } = "srt";   // "srt" or "vtt"
    public bool EmbedSubtitles { get; init; } = true;      // true = embed into file, false = save alongside

    public bool EmbedMetadata { get; init; } = true;

    /// <summary>Path to a Netscape-format cookies.txt file, passed to yt-dlp via --cookies (needed for login-gated sites like X/Twitter). Empty = no cookies.</summary>
    public string CookiesPath { get; init; } = "";

    // "embed" -> --embed-thumbnail, "save" -> write thumbnail alongside file, "none" -> skip
    public string ThumbnailMode { get; init; } = "embed";
}

public class DownloadProgress
{
    public double Percent { get; init; }
    public string StatusText { get; init; } = "";
}

/// <summary>Instant title/author/thumbnail from YouTube's oEmbed endpoint (used to paint the options card while the full dump runs).</summary>
public record YouTubePreview(string Title, string Author, string ThumbnailUrl);

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

    /// <summary>
    /// yt-dlp info JSON from the analyze step (empty = none). When fresh, the download loads it with
    /// <c>--load-info-json</c> instead of extracting the page again.
    /// </summary>
    public string InfoJson { get; set; } = "";
    public DateTime InfoJsonAnalyzedAtUtc { get; init; }

    /// <summary>When the user asked for this download. Feeds the date/time file-name prefix, so a paused
    /// and resumed job keeps the same file names (and finds its partial files again).</summary>
    public DateTime RequestedAt { get; init; } = DateTime.Now;

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
    public string CookiesPath { get; set; } = "";

    // "embed" -> --embed-thumbnail, "save" -> write thumbnail alongside file, "none" -> skip
    public string ThumbnailMode { get; init; } = "embed";
}

public class DownloadProgress
{
    public double Percent { get; init; }
    public string StatusText { get; init; } = "";

    // Filled by the multi-connection engine (zero / null for plain yt-dlp progress lines).
    public double BytesPerSecond { get; init; }
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public TimeSpan? Eta { get; init; }
    public int Connections { get; init; }

    /// <summary>Per-segment state of the file being fetched, for an IDM-style segment bar. Positions are relative to <see cref="SegmentsTotal"/>.</summary>
    public IReadOnlyList<y2akio.Services.Downloads.SegmentSnapshot>? Segments { get; init; }
    public long SegmentsTotal { get; init; }
}

/// <summary>Settings for the multi-connection engine on one download.</summary>
public sealed class DownloadEngineOptions
{
    /// <summary>False = let yt-dlp download everything itself (still with retries and parallel fragments).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Connections per file (and parallel fragments for HLS/DASH).</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>Speed limiters this download counts against (global, per job).</summary>
    public IReadOnlyList<y2akio.Services.Downloads.RateLimiter> RateLimiters { get; init; } = Array.Empty<y2akio.Services.Downloads.RateLimiter>();

    /// <summary>Told about every partial file the engine starts, so a cancelled job can delete them.</summary>
    public Action<string>? OnPartialFile { get; init; }

    /// <summary>The tightest active limit in bytes/s (0 = none), for yt-dlp's own --limit-rate.</summary>
    public long CurrentRateLimitBytes()
    {
        long limit = 0;
        foreach (var l in RateLimiters)
        {
            var v = l.BytesPerSecond;
            if (v > 0 && (limit == 0 || v < limit)) limit = v;
        }
        return limit;
    }
}

/// <summary>Instant title/author/thumbnail from YouTube's oEmbed endpoint (used to paint the options card while the full dump runs).</summary>
public record YouTubePreview(string Title, string Author, string ThumbnailUrl);

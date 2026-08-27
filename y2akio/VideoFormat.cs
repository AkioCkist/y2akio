using System.Text.Json;

namespace y2akio.Models;

/// <summary>
/// A single entry from yt-dlp's "formats" array in --dump-json output.
/// Only the fields we actually use are pulled out; everything else is ignored.
/// </summary>
public class VideoFormat
{
    public string FormatId { get; init; } = "";
    public string Extension { get; init; } = "";
    public string? Resolution { get; init; }      // e.g. "1920x1080" or null for audio-only
    public int? Height { get; init; }              // e.g. 1080
    public double? AudioBitrateKbps { get; init; }  // abr
    public double? VideoBitrateKbps { get; init; }  // vbr
    public long? FileSizeBytes { get; init; }
    public string? FormatNote { get; init; }        // e.g. "1080p", "medium"
    public bool HasVideo { get; init; }
    public bool HasAudio { get; init; }

    public bool IsAudioOnly => HasAudio && !HasVideo;
    public bool IsVideoOnly => HasVideo && !HasAudio;

    /// <summary>Friendly label used to populate the "Quality / Format" dropdown.</summary>
    public string DisplayLabel
    {
        get
        {
            if (IsAudioOnly)
            {
                var kbps = AudioBitrateKbps.HasValue ? $"{AudioBitrateKbps.Value:0} kbps" : FormatNote ?? Extension;
                return $"{Extension.ToUpperInvariant()} {kbps}";
            }

            var res = Height.HasValue ? $"{Height}p" : (FormatNote ?? Resolution ?? Extension);
            return $"{res} ({Extension.ToUpperInvariant()})";
        }
    }

    public static VideoFormat FromJson(JsonElement el)
    {
        string GetStr(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        double? GetNum(string name) =>
            el.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.Number) ? v.GetDouble() : (double?)null;

        var vcodec = GetStr("vcodec");
        var acodec = GetStr("acodec");

        return new VideoFormat
        {
            FormatId = GetStr("format_id"),
            Extension = GetStr("ext"),
            Resolution = el.TryGetProperty("resolution", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
            Height = GetNum("height").HasValue ? (int)GetNum("height")!.Value : null,
            AudioBitrateKbps = GetNum("abr"),
            VideoBitrateKbps = GetNum("vbr"),
            FileSizeBytes = GetNum("filesize").HasValue ? (long)GetNum("filesize")!.Value
                          : GetNum("filesize_approx").HasValue ? (long)GetNum("filesize_approx")!.Value
                          : null,
            FormatNote = GetStr("format_note"),
            HasVideo = !string.IsNullOrEmpty(vcodec) && vcodec != "none",
            HasAudio = !string.IsNullOrEmpty(acodec) && acodec != "none",
        };
    }
}

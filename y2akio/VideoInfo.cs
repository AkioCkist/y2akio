using System.Collections.Generic;
using System.Text.Json;

namespace y2akio.Models;

/// <summary>Parsed result of `yt-dlp --dump-single-json <url>`.</summary>
public class VideoInfo
{
    public string Title { get; init; } = "";
    public string Uploader { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public double DurationSeconds { get; init; }
    public string OriginalUrl { get; init; } = "";
    public List<VideoFormat> Formats { get; init; } = new();

    public string DurationDisplay
    {
        get
        {
            var t = System.TimeSpan.FromSeconds(DurationSeconds);
            return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    public static VideoInfo FromJson(string json, string originalUrl)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string GetStr(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var formats = new List<VideoFormat>();
        if (root.TryGetProperty("formats", out var fmts) && fmts.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in fmts.EnumerateArray())
                formats.Add(VideoFormat.FromJson(f));
        }

        double duration = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : 0;

        return new VideoInfo
        {
            Title = GetStr("title"),
            Uploader = string.IsNullOrEmpty(GetStr("uploader")) ? GetStr("channel") : GetStr("uploader"),
            ThumbnailUrl = GetStr("thumbnail"),
            DurationSeconds = duration,
            OriginalUrl = originalUrl,
            Formats = formats,
        };
    }
}

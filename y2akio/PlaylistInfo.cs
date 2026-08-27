using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace y2akio.Models;

/// <summary>Result of analyzing a link: either a single video or a playlist.</summary>
public class AnalyzeResult
{
    public bool IsPlaylist { get; init; }
    public PlaylistInfo? Playlist { get; init; }
    public VideoInfo? Video { get; init; }
}

/// <summary>
/// A parsed playlist, produced from a `--flat-playlist --dump-single-json` run.
/// Entries carry only basic metadata — per-video formats are fetched lazily
/// (see <see cref="YtDlpService.AnalyzeVideoAsync"/>) when a track is clicked.
/// </summary>
public class PlaylistInfo
{
    public string Title { get; init; } = "";
    public string Uploader { get; init; } = "";
    public int Count { get; init; }
    public string OriginalUrl { get; init; } = "";
    public List<PlaylistEntry> Entries { get; init; } = new();

    public static PlaylistInfo FromFlatJson(JsonElement root, string originalUrl)
    {
        string GetStr(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var entries = new List<PlaylistEntry>();
        if (root.TryGetProperty("entries", out var entriesEl) && entriesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in entriesEl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var entry = PlaylistEntry.FromFlatJson(e, originalUrl);
                if (!string.IsNullOrWhiteSpace(entry.Url)) entries.Add(entry);
            }
        }

        return new PlaylistInfo
        {
            Title = GetStr("title"),
            Uploader = string.IsNullOrEmpty(GetStr("uploader")) ? GetStr("channel") : GetStr("uploader"),
            Count = entries.Count,
            OriginalUrl = originalUrl,
            Entries = entries,
        };
    }
}

/// <summary>One track in a playlist, parsed from flat-playlist mode (no formats yet).</summary>
public class PlaylistEntry : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string ThumbnailUrl { get; init; } = "";
    public double DurationSeconds { get; init; }

    private bool _selected = true;
    /// <summary>Whether this track should be included in a batch playlist download.</summary>
    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; OnPropertyChanged(); }
    }

    public string DurationDisplay
    {
        get
        {
            if (DurationSeconds <= 0) return "—";
            var t = System.TimeSpan.FromSeconds(DurationSeconds);
            return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    public static PlaylistEntry FromFlatJson(JsonElement el, string playlistUrl)
    {
        string GetStr(string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        double duration = el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetDouble()
            : 0;

        var id = GetStr("id");
        var url = GetStr("url");
        if (string.IsNullOrWhiteSpace(url)) url = GetStr("webpage_url");
        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(id))
        {
            // Best-effort reconstruction: YouTube always accepts the watch URL form.
            url = playlistUrl.Contains("youtube.com") || playlistUrl.Contains("youtu.be")
                ? $"https://www.youtube.com/watch?v={id}"
                : $"{playlistUrl}?v={id}";
        }

        var thumb = GetStr("thumbnail");
        if (string.IsNullOrWhiteSpace(thumb)
            && el.TryGetProperty("thumbnails", out var thumbs)
            && thumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in thumbs.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Object
                    && t.TryGetProperty("url", out var tu)
                    && tu.ValueKind == JsonValueKind.String)
                {
                    thumb = tu.GetString() ?? "";
                }
            }
        }

        var title = GetStr("title");
        return new PlaylistEntry
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? $"(untitled {id})" : title,
            Url = url,
            ThumbnailUrl = thumb,
            DurationSeconds = duration,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

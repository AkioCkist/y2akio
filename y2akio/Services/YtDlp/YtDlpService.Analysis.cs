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

/// <summary>URL analysis, normalization, retry policy, and preview metadata.</summary>
public partial class YtDlpService
{
    /// <summary>
    /// Analyzes a pasted link. Runs a fast <c>--flat-playlist</c> probe first. Playlists return
    /// entry metadata; definite single-video URLs go directly to the full format analysis.
    /// </summary>
    /// <param name="cookiesPath">Optional Netscape cookies file for login-gated sites.</param>
    public async Task<AnalyzeResult> AnalyzeAsync(string url, string? cookiesPath = null, CancellationToken ct = default)
    {
        // Fast path: a URL that is definitely one video needs no playlist probe — a flat probe on a
        // single video costs a full extraction (~3s) AND still returns the full formats array, so it
        // would only be wasted work before the real dump below. Go straight to the single dump.
        if (LooksLikeSingleVideoUrl(url))
            return new AnalyzeResult { IsPlaylist = false, Video = await AnalyzeVideoAsync(url, cookiesPath, ct) };

        var flatJson = await RunDumpJsonAsync(url, flat: true, cookiesPath, ct);

        using (var doc = JsonDocument.Parse(flatJson))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var entriesEl)
                && entriesEl.ValueKind == JsonValueKind.Array
                && entriesEl.GetArrayLength() > 1)
            {
                var playlist = PlaylistInfo.FromFlatJson(root, url);
                return new AnalyzeResult { IsPlaylist = true, Playlist = playlist };
            }
        }

        var video = await AnalyzeVideoAsync(url, cookiesPath, ct);
        return new AnalyzeResult { IsPlaylist = false, Video = video };
    }

    /// <summary>Runs a full (non-flat) `yt-dlp --dump-single-json <url>` for one specific video, formats included.</summary>
    /// <param name="cookiesPath">Netscape cookies.txt path for login-gated sites (X/Twitter, ...). Null/empty = no cookies.</param>
    public async Task<VideoInfo> AnalyzeVideoAsync(string url, string? cookiesPath = null, CancellationToken ct = default)
    {
        var json = await RunDumpJsonAsync(url, flat: false, cookiesPath, ct);
        return VideoInfo.FromJson(json, url);
    }

    /// <summary>
    /// Cheap YouTube-only title/author/thumbnail via the oEmbed endpoint (~0.2s, no yt-dlp process).
    /// Used to paint the options card while the full dump is still running. Never throws — returns null
    /// for non-YouTube URLs, HTTP errors, and unparseable/blocked responses (age-restricted, private...).
    /// </summary>
    public async Task<YouTubePreview?> FetchYouTubePreviewAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (!IsYouTubeUrl(url)) return null;

            var endpoint = $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(url)}&format=json";
            using var response = await _http.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string GetStr(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var title = GetStr("title");
            if (string.IsNullOrWhiteSpace(title)) return null;
            return new YouTubePreview(title, GetStr("author_name"), GetStr("thumbnail_url"));
        }
        catch
        {
            return null; // oEmbed is a best-effort nicety; the full dump paints the card anyway
        }
    }

    private async Task<string> RunDumpJsonAsync(string url, bool flat, string? cookiesPath, CancellationToken ct)
    {
        url = NormalizeUrl(url);

        string lastError = "";
        foreach (var attemptArgs in AttemptArgs(url, cookiesPath))
        {
            var args = new StringBuilder()
                .Append("--dump-single-json --no-warnings ")
                .Append(flat ? "--flat-playlist --playlist-items 1-500 " : "--no-playlist ")
                .Append(FfmpegLocationArg())
                .Append(ImpersonateArg(url))
                .Append(attemptArgs)
                .Append($"\"{url}\"");

            var (stdout, stderr, exitCode) = await RunProcessAsync(args.ToString(), ct);
            lastError = stderr;
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout)) return stdout;
            if (!IsRetryable(url, lastError)) break;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(lastError) ? "yt-dlp failed to analyze this link." : lastError.Trim());
    }

    /// <summary>
    /// The yt-dlp variants to try in order, each as extra CLI args (cookies / client overrides):
    /// <list type="bullet">
    /// <item>YouTube: web client, then the Android player client. The web client is increasingly
    /// bot-blocked for specific videos ("This video is not available" / "The page needs to be
    /// reloaded"); Android is far less aggressive about bot checks (trade-off: a lower format
    /// ceiling — some videos only surface 360p through it).</item>
    /// <item>Facebook with cookies: anonymous first, then with cookies. A logged-in session gets a
    /// different page layout that yt-dlp's facebook extractor can't read ("No video formats found!"),
    /// while public videos extract fine anonymously; cookies remain the fallback for videos only
    /// visible when logged in (groups, friends-only).</item>
    /// <item>Everything else: one run, with cookies when imported.</item>
    /// </list>
    /// </summary>
    private static string[] AttemptArgs(string url, string? cookiesPath)
    {
        if (IsYouTubeUrl(url)) return new[] { "", "--extractor-args \"youtube:player_client=android\" " };

        var cookies = CookiesArg(cookiesPath, url);
        if (IsFacebookUrl(url) && cookies.Length > 0) return new[] { "", cookies };
        return new[] { cookies };
    }

    /// <summary>Whether a failed attempt is worth the next one in <see cref="AttemptArgs"/>.</summary>
    private static bool IsRetryable(string url, string error) =>
        !IsYouTubeUrl(url) || IsYoutubeClientRetryable(error);

    /// <summary>
    /// Passes the imported cookie jar to yt-dlp — except for YouTube. The imported file is the
    /// browser's *whole* jar, and sending it to YouTube trips Google's bot check
    /// ("The page needs to be reloaded"); YouTube extracts fine anonymously. Cookies only matter
    /// for login-gated content (X/Twitter, Facebook private/stories, ...).
    /// </summary>
    private static string CookiesArg(string? cookiesPath, string url)
    {
        if (string.IsNullOrWhiteSpace(cookiesPath)) return "";
        if (IsYouTubeUrl(url)) return "";
        return $"--cookies \"{cookiesPath}\" ";
    }

    private static bool IsYouTubeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True only when the URL *definitely* points at ONE video (so the playlist probe can be skipped —
    /// it costs a full extraction on a single video and still returns full formats). Conservative by
    /// design: anything ambiguous (channels, /playlist, list= params, non-YouTube hosts) returns false
    /// and keeps the flat-probe → playlist-detection path.
    /// </summary>
    public static bool LooksLikeSingleVideoUrl(string url)
    {
        url = NormalizeUrl(url); // same normalization RunDumpJsonAsync applies
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsYouTubeUrl(url)) return false;

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=', 2)[0];
            if (string.Equals(key, "list", StringComparison.OrdinalIgnoreCase)) return false; // in a playlist / watch-later
        }

        var host = uri.Host;
        var isShortHost = string.Equals(host, "youtu.be", StringComparison.OrdinalIgnoreCase);
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (isShortHost) return segs.Length == 1 && !string.IsNullOrWhiteSpace(segs[0]);

        if (segs.Length == 1 && string.Equals(segs[0], "watch", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2
                    && string.Equals(parts[0], "v", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(parts[1]))
                {
                    return true;
                }
            }
            return false;
        }

        // /shorts/<id>, /embed/<id>, /live/<id>, /v/<id>
        return segs.Length == 2
            && segs[0] is "shorts" or "embed" or "live" or "v"
            && !string.IsNullOrWhiteSpace(segs[1]);
    }

    /// <summary>
    /// Facebook/Instagram regularly bot-blocks plain requests, which surfaces as
    /// "[facebook] ...: Cannot parse data". `--impersonate chrome` (curl_cffi TLS fingerprint)
    /// makes the request look like a real browser — the bundled yt-dlp.exe supports it.
    /// Scoped to Facebook hosts so other extractors are unaffected.
    /// </summary>
    private static string ImpersonateArg(string url) =>
        IsFacebookUrl(url) ? "--impersonate chrome " : "";

    private static bool IsFacebookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("facebook.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("fb.watch", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// yt-dlp's facebook extractor does not recognize `/stories/<id>/<token>/` URLs at all
    /// ("Unsupported URL"), even with valid cookies. But the story ID is the video ID, and the
    /// extractor *does* handle `watch/?v=&lt;id&gt;` — so rewrite story links to that form
    /// before passing them on. (Anonymous story links also just redirect to login.php.)
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.EndsWith("facebook.com", StringComparison.OrdinalIgnoreCase)) return url;
        if (!uri.AbsolutePath.StartsWith("/stories/", StringComparison.OrdinalIgnoreCase)) return url;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !long.TryParse(segments[1], out _)) return url;

        return $"{uri.Scheme}://{uri.Host}/watch/?v={segments[1]}";
    }

    /// <summary>
    /// Downloads according to the given request, reporting progress parsed from yt-dlp's stdout.
    /// Returns the final saved file path (best-effort — yt-dlp prints it in a "[download] Destination: ..."
    /// or "[Merger] Merging formats into ..." line).
    /// </summary>
    /// <param name="onLogLine">Receives every raw stdout/stderr line (progress, ffmpeg, errors) for a debug log.</param>
}

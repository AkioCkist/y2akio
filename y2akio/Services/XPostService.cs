using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// Photos of an X (Twitter) post (<c>x.com/&lt;user&gt;/status/&lt;id&gt;</c>). yt-dlp's twitter extractor
/// only handles video ("No video could be found in this tweet"), so photo posts are fetched here:
/// <list type="number">
/// <item>The public embed API (<c>cdn.syndication.twimg.com/tweet-result</c>) — no login, covers
/// public posts. <c>mediaDetails[]</c> holds photos and video variants.</item>
/// <item>For posts it won't serve (protected accounts, age-restricted, login-only), X's own web
/// GraphQL query <c>TweetResultByRestId</c> with the imported cookies (<c>auth_token</c> + <c>ct0</c>).
/// Its persisted query id rotates with X's web deploys, so a stale id is re-discovered from X's
/// JS bundles.</item>
/// </list>
/// Photos download in original quality (<c>?name=orig</c>). Video-only posts return null so the
/// caller hands them to yt-dlp (full format choice).
/// </summary>
public class XPostService
{
    public const string Referer = "https://x.com/";

    // Public bearer token of X's own web client — the same one every browser session sends.
    private const string WebBearer =
        "AAAAAAAAAAAAAAAAAAAAANRILgAAAAAAnNwIzUejRCOuH5E6I8xnZz4puTs%3D1Zv7ttfk8LF81IUq16cHjhLTvJu4FA33AGWWjCpTnA";

    private static readonly Regex StatusRegex = new(@"/status(?:es)?/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Last known TweetResultByRestId operation (Relay persisted query) — refreshed from X's bundles when it goes stale.
    private static string _queryId = "vEO-5QkqZwFZlJZoNTs6fA";
    private static string[] _providedVariables =
    {
        "__relay_internal__pv__xclientsfeaturesappviewerisloggedinrelayprovider",
        "__relay_internal__pv__xclientsfeaturestranslationnoteautotranslationenabledrelayprovider",
    };

    /// <summary>True for <c>x.com</c> / <c>twitter.com</c> status links (any subdomain, <c>/i/status/…</c> and <c>/photo/N</c> suffixes included).</summary>
    public static bool IsPostUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (MediaGalleryService.IsHostOf(uri.Host, "x.com") || MediaGalleryService.IsHostOf(uri.Host, "twitter.com"))
        && StatusRegex.IsMatch(uri.AbsolutePath);

    /// <param name="cookiesText">Netscape cookies.txt content; only x.com entries are sent, and only for the logged-in fallback.</param>
    /// <returns>The post's photos (plus any videos mixed in), or null for a video-only post (left to yt-dlp).</returns>
    public async Task<MediaGalleryPost?> FetchPostAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var tweetId = StatusRegex.Match(new Uri(url).AbsolutePath).Groups[1].Value;

        // 1) Public embed API.
        var tweet = await TrySyndicationAsync(tweetId, ct);
        if (tweet is not null) return ToPost(tweet, tweetId);

        // 2) Logged-in web API.
        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "x.com"));
        if (!cookieHeader.Contains("auth_token=") || !cookieHeader.Contains("ct0="))
        {
            throw new InvalidOperationException(
                "X didn't serve this post publicly (protected account, age-restricted, or deleted). If you can see it while logged in, import your browser's cookies.txt (with a logged-in X session) in Settings.");
        }

        tweet = await FetchGraphQlAsync(tweetId, cookieHeader, ct)
            ?? throw new InvalidOperationException(
                "X returned no data for this post. It may be deleted, or from a protected account your imported cookies' account doesn't follow.");
        return ToPost(tweet, tweetId);
    }

    // ─── Common tweet shape ──────────────────────────────────────────────────

    private sealed record TweetMedia(string Type, string Url, int Width, int Height, List<string> VideoUrls);
    private sealed record Tweet(string ScreenName, string Name, string Text, List<TweetMedia> Media);

    private static MediaGalleryPost? ToPost(Tweet tweet, string tweetId)
    {
        var items = new List<MediaGalleryItem>();
        foreach (var m in tweet.Media)
        {
            if (m.Type == "photo")
            {
                items.Add(new MediaGalleryItem
                {
                    Index = items.Count + 1,
                    Urls = OriginalPhotoUrls(m.Url),
                    Width = m.Width,
                    Height = m.Height,
                });
            }
            else if (m.VideoUrls.Count > 0) // video / animated_gif
            {
                items.Add(new MediaGalleryItem
                {
                    Index = items.Count + 1,
                    IsVideo = true,
                    Urls = m.VideoUrls,
                    ThumbnailUrls = new() { m.Url },
                });
            }
        }

        // Video-only posts go to yt-dlp, which offers the full format/quality choice.
        if (!items.Any(i => !i.IsVideo)) return null;

        return new MediaGalleryPost
        {
            Source = "X Post",
            Id = tweetId,
            Author = string.IsNullOrWhiteSpace(tweet.ScreenName) ? "x" : tweet.ScreenName,
            Nickname = tweet.Name,
            Title = FirstLine(tweet.Text),
            Items = items,
            Referer = Referer,
        };
    }

    /// <summary>"https://pbs.twimg.com/media/ABC.jpg" → original-quality URL first, the plain URL as fallback.</summary>
    private static List<string> OriginalPhotoUrls(string url)
    {
        var m = Regex.Match(url, @"^(https://pbs\.twimg\.com/media/[^.?]+)\.(\w+)");
        return m.Success
            ? new List<string> { $"{m.Groups[1].Value}?format={m.Groups[2].Value}&name=orig", url }
            : new List<string> { url };
    }

    // ─── 1) Syndication ──────────────────────────────────────────────────────

    private static async Task<Tweet?> TrySyndicationAsync(string tweetId, CancellationToken ct)
    {
        try
        {
            var url = $"https://cdn.syndication.twimg.com/tweet-result?id={tweetId}&lang=en&token={SyndicationToken(tweetId)}";
            using var response = await MediaGalleryService.Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (Str(root, "__typename") is "TweetTombstone" or "TweetUnavailable") return null;
            if (!root.TryGetProperty("mediaDetails", out var details) || details.ValueKind != JsonValueKind.Array) return null;

            var user = root.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
            return new Tweet(
                user.ValueKind == JsonValueKind.Object ? Str(user, "screen_name") : "",
                user.ValueKind == JsonValueKind.Object ? Str(user, "name") : "",
                StripTrailingLink(Str(root, "text")),
                details.EnumerateArray().Select(ParseMediaEntity).ToList());
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null; // fall through to the logged-in API
        }
    }

    /// <summary>The embed API's token: (id / 1e15 · π) in base 36, zeros and the dot removed (mirrors X's embed script).</summary>
    private static string SyndicationToken(string tweetId)
    {
        var x = double.Parse(tweetId, CultureInfo.InvariantCulture) / 1e15 * Math.PI;
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";

        var intPart = (long)Math.Floor(x);
        var frac = x - intPart;
        var sb = new StringBuilder();
        do { sb.Insert(0, digits[(int)(intPart % 36)]); intPart /= 36; } while (intPart > 0);
        sb.Append('.');
        for (var i = 0; i < 12 && frac > 0; i++)
        {
            frac *= 36;
            var d = (int)Math.Floor(frac);
            sb.Append(digits[d]);
            frac -= d;
        }
        return Regex.Replace(sb.ToString(), @"(0+|\.)", "");
    }

    // ─── 2) GraphQL (logged in) ──────────────────────────────────────────────

    private static async Task<Tweet?> FetchGraphQlAsync(string tweetId, string cookieHeader, CancellationToken ct)
    {
        var (status, json) = await QueryTweetAsync(tweetId, cookieHeader, ct);

        // 404 / validation error = the persisted query id rotated: re-discover it and retry once.
        if (status is 400 or 404 or 422)
        {
            if (await DiscoverQueryAsync(ct)) (status, json) = await QueryTweetAsync(tweetId, cookieHeader, ct);
        }
        if (status == 401 || status == 403)
            throw new InvalidOperationException("X rejected the imported cookies (logged out or expired). Re-export cookies.txt from a logged-in browser and import it in Settings.");
        if (status == 429)
            throw new InvalidOperationException("X is rate-limiting requests right now. Wait a few minutes and try again.");
        if (status != 200) return null;

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("tweet_result_by_rest_id", out var holder)
            || !holder.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        // "TweetWithVisibilityResults" wraps the tweet for limited-visibility posts.
        if (result.TryGetProperty("tweet", out var inner) && inner.ValueKind == JsonValueKind.Object) result = inner;
        if (Str(result, "__typename") is "TweetTombstone" or "TweetUnavailable") return null;

        var media = new List<TweetMedia>();
        foreach (var key in new[] { "media_entities2", "media_entities" })
        {
            if (result.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                media.AddRange(arr.EnumerateArray().Select(ParseMediaEntity));
                break;
            }
        }
        // Older response shape: legacy.extended_entities.media.
        if (media.Count == 0
            && result.TryGetProperty("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.Object
            && legacy.TryGetProperty("extended_entities", out var ext) && ext.ValueKind == JsonValueKind.Object
            && ext.TryGetProperty("media", out var legacyMedia) && legacyMedia.ValueKind == JsonValueKind.Array)
        {
            media.AddRange(legacyMedia.EnumerateArray().Select(ParseMediaEntity));
        }

        string screenName = "", name = "";
        if (result.TryGetProperty("core", out var core) && core.ValueKind == JsonValueKind.Object
            && core.TryGetProperty("user_results", out var ur) && ur.ValueKind == JsonValueKind.Object
            && ur.TryGetProperty("result", out var user) && user.ValueKind == JsonValueKind.Object)
        {
            var names = user.TryGetProperty("core", out var uc) && uc.ValueKind == JsonValueKind.Object ? uc
                : user.TryGetProperty("legacy", out var ul) && ul.ValueKind == JsonValueKind.Object ? ul
                : default;
            if (names.ValueKind == JsonValueKind.Object)
            {
                screenName = Str(names, "screen_name");
                name = Str(names, "name");
            }
        }

        var text = result.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object ? Str(det, "full_text")
            : result.TryGetProperty("legacy", out var lg) && lg.ValueKind == JsonValueKind.Object ? Str(lg, "full_text")
            : "";
        return new Tweet(screenName, name, StripTrailingLink(text), media);
    }

    private static async Task<(int Status, string Json)> QueryTweetAsync(string tweetId, string cookieHeader, CancellationToken ct)
    {
        var variables = new Dictionary<string, object> { ["restId"] = tweetId, ["withFocalTweet"] = true };
        foreach (var pv in _providedVariables) variables[pv] = pv.Contains("loggedin", StringComparison.OrdinalIgnoreCase);

        var url = $"https://x.com/i/api/graphql/{_queryId}/TweetResultByRestId?variables={Uri.EscapeDataString(JsonSerializer.Serialize(variables))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + WebBearer);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("x-twitter-auth-type", "OAuth2Session");
        request.Headers.TryAddWithoutValidation("x-twitter-active-user", "yes");
        request.Headers.Referrer = new Uri(Referer);
        var csrf = Regex.Match(cookieHeader, @"(?:^|; )ct0=([^;]+)");
        if (csrf.Success) request.Headers.TryAddWithoutValidation("x-csrf-token", csrf.Groups[1].Value);

        using var response = await MediaGalleryService.Http.SendAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Finds the current <c>TweetResultByRestId</c> persisted query id (and its provided variables) by
    /// walking X's web bundles from the home page's entry script (breadth-first, bounded).
    /// </summary>
    private static async Task<bool> DiscoverQueryAsync(CancellationToken ct)
    {
        try
        {
            var (home, _) = await MediaGalleryService.GetPageAsync("https://x.com/", "", ct);
            var roots = Regex.Matches(home, @"https://abs\.twimg\.com/[^""'\s]+\.js").Select(m => m.Value);
            // Two queues: chunks whose names look tweet-related are scanned first, the rest only if needed.
            var likely = new Queue<string>();
            var other = new Queue<string>(roots);
            var seen = new HashSet<string>();

            while ((likely.Count > 0 || other.Count > 0) && seen.Count < 200)
            {
                var scriptUrl = likely.Count > 0 ? likely.Dequeue() : other.Dequeue();
                if (!seen.Add(scriptUrl)) continue;

                string js;
                try { js = await MediaGalleryService.Http.GetStringAsync(scriptUrl, ct); }
                catch (HttpRequestException) { continue; }

                var op = Regex.Match(js, @"params:\{id:`([^`]+)`,metadata:\{\},name:`TweetResultByRestId`[^}]*?providedVariables:\{([^}]*)\}");
                if (op.Success)
                {
                    _queryId = op.Groups[1].Value;
                    _providedVariables = Regex.Matches(op.Groups[2].Value, @"__relay_internal__pv__\w+").Select(m => m.Value).Distinct().ToArray();
                    return true;
                }

                // Chunks are referenced relative to the importing script ("./assets/x.js") or absolutely.
                var dir = scriptUrl[..(scriptUrl.LastIndexOf('/') + 1)];
                foreach (Match imp in Regex.Matches(js, @"[""'`](\./[\w./-]+\.js|https://abs\.twimg\.com/[^""'`\s]+\.js)[""'`]"))
                {
                    var next = imp.Groups[1].Value.StartsWith("./") ? dir + imp.Groups[1].Value[2..] : imp.Groups[1].Value;
                    if (seen.Contains(next)) continue;
                    var name = next[(next.LastIndexOf('/') + 1)..];
                    (Regex.IsMatch(name, "tweet|status|article|conversation", RegexOptions.IgnoreCase) ? likely : other).Enqueue(next);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* keep the old id */ }
        return false;
    }

    // ─── Parse helpers ───────────────────────────────────────────────────────

    /// <summary>One media entity (same shape in the embed API, GraphQL <c>media_entities2</c> and legacy entities).</summary>
    private static TweetMedia ParseMediaEntity(JsonElement m)
    {
        var type = Str(m, "type");
        var url = Str(m, "media_url_https");
        int w = 0, h = 0;
        if (m.TryGetProperty("original_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            w = Int(info, "width");
            h = Int(info, "height");
        }

        var videos = new List<string>();
        if (m.TryGetProperty("video_info", out var vi) && vi.ValueKind == JsonValueKind.Object
            && vi.TryGetProperty("variants", out var variants) && variants.ValueKind == JsonValueKind.Array)
        {
            videos = variants.EnumerateArray()
                .Where(v => Str(v, "content_type") == "video/mp4" && Str(v, "url").Length > 0)
                .OrderByDescending(v => Int(v, "bitrate"))
                .Select(v => Str(v, "url"))
                .ToList();
        }
        return new TweetMedia(type, url, w, h, videos);
    }

    /// <summary>Drops the trailing t.co link X appends for attached media.</summary>
    private static string StripTrailingLink(string text) =>
        Regex.Replace(text, @"\s*https://t\.co/\w+\s*$", "").Trim();

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : 0;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// Instagram stories (<c>instagram.com/stories/&lt;username&gt;/</c>) and story highlights
/// (<c>instagram.com/stories/highlights/&lt;id&gt;/</c>). Login-only, so the imported cookies are required.
/// The story page already embeds the reel in its Relay payload
/// (<c>xdt_api__v1__feed__reels_media.reels_media[]</c>); when it doesn't, the same data comes from the
/// web API <c>/api/v1/feed/reels_media/?reel_ids=…</c>. Items are <c>media_type</c> 1 (photo,
/// <c>image_versions2.candidates</c>) or 2 (video, <c>video_versions</c> — progressive mp4 with audio).
/// </summary>
public class InstagramStoryService
{
    public const string Referer = "https://www.instagram.com/";
    private const string WebAppId = "936619743392459"; // instagram.com's own web client id, required by the web API

    private static readonly Regex JsonScriptRegex = new(
        @"<script type=""application/json""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>True for <c>instagram.com/stories/...</c> links (user stories and highlights).</summary>
    public static bool IsStoryUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && MediaGalleryService.IsHostOf(uri.Host, "instagram.com")
        && uri.AbsolutePath.StartsWith("/stories/", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    /// <param name="cookiesText">Netscape cookies.txt content; only instagram.com entries are sent. Stories need a logged-in session.</param>
    public async Task<MediaGalleryPost> FetchStoryAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var segments = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var isHighlight = segments[1].Equals("highlights", StringComparison.OrdinalIgnoreCase);
        if (isHighlight && segments.Length < 3)
            throw new InvalidOperationException("This Instagram highlight link is missing its id.");
        var username = isHighlight ? "" : segments[1];
        var reelId = isHighlight ? $"highlight:{segments[2]}" : "";

        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "instagram.com"));
        if (!cookieHeader.Contains("sessionid="))
            throw new InvalidOperationException(
                "Instagram stories are only visible when logged in. Import your browser's cookies.txt (with a logged-in Instagram session) in Settings.");

        var pageUrl = isHighlight
            ? $"https://www.instagram.com/stories/highlights/{segments[2]}/"
            : $"https://www.instagram.com/stories/{username}/";
        var (html, finalUri) = await MediaGalleryService.GetPageAsync(pageUrl, cookieHeader, ct);
        if (finalUri.AbsolutePath.StartsWith("/accounts/login", StringComparison.OrdinalIgnoreCase)
            || finalUri.AbsolutePath.Contains("challenge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Instagram sent this request to the login page — the imported cookies have expired. Re-export cookies.txt from a logged-in browser and import it in Settings.");
        }

        // 1) Reel embedded in the page.
        var post = FindReel(JsonScriptRegex.Matches(html).Select(m => m.Groups[1].Value), username, reelId);

        // 2) Web API fallback (user stories need the numeric user id first).
        if (post is null)
        {
            if (!isHighlight) reelId = await LookupUserIdAsync(username, cookieHeader, ct);
            var json = await GetApiAsync($"https://www.instagram.com/api/v1/feed/reels_media/?reel_ids={Uri.EscapeDataString(reelId)}", cookieHeader, ct);
            post = FindReel(new[] { json }, username, reelId);
        }

        if (post is null || post.Items.Count == 0)
            throw new InvalidOperationException(
                "No active stories found. Stories disappear after 24 hours, and private accounts are only visible if you follow them.");
        return post;
    }

    // ─── API ─────────────────────────────────────────────────────────────────

    internal static async Task<string> GetApiAsync(string url, string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Headers.TryAddWithoutValidation("X-IG-App-ID", WebAppId);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = new Uri(Referer);
        var csrf = Regex.Match(cookieHeader, @"(?:^|; )csrftoken=([^;]+)");
        if (csrf.Success) request.Headers.TryAddWithoutValidation("X-CSRFToken", csrf.Groups[1].Value);

        using var response = await MediaGalleryService.Http.SendAsync(request, ct);
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Instagram is rate-limiting requests right now. Wait a few minutes and try again.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static async Task<string> LookupUserIdAsync(string username, string cookieHeader, CancellationToken ct)
    {
        var json = await GetApiAsync(
            $"https://www.instagram.com/api/v1/users/web_profile_info/?username={Uri.EscapeDataString(username)}", cookieHeader, ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            && Str(user, "id") is { Length: > 0 } id)
        {
            return id;
        }
        throw new InvalidOperationException($"Instagram user @{username} wasn't found.");
    }

    // ─── Parse ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches the given JSON blobs for <c>reels_media</c> arrays (same shape in the page payload and
    /// the API response) and returns the reel for <paramref name="username"/> / <paramref name="reelId"/>.
    /// </summary>
    private static MediaGalleryPost? FindReel(IEnumerable<string> jsonBlobs, string username, string reelId)
    {
        foreach (var json in jsonBlobs)
        {
            if (!json.Contains("reels_media", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var reels = new List<JsonElement>();
                FindReels(doc.RootElement, reels, depth: 0);

                foreach (var reel in reels)
                {
                    var user = reel.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                    var reelUser = user.ValueKind == JsonValueKind.Object ? Str(user, "username") : "";
                    var matches = reelId.Length > 0
                        ? IdOf(reel) == reelId || (!reelId.StartsWith("highlight:") && IdOf(user) == reelId)
                        : reelUser.Equals(username, StringComparison.OrdinalIgnoreCase);
                    if (!matches) continue;

                    var items = ParseItems(reel);
                    if (items.Count == 0) continue;

                    var author = reelUser.Length > 0 ? reelUser : username;
                    return new MediaGalleryPost
                    {
                        Source = reelId.StartsWith("highlight:") ? "Instagram Highlight" : "Instagram Story",
                        Id = reelId.StartsWith("highlight:") ? reelId["highlight:".Length..] : IdOf(user) is { Length: > 0 } pk ? pk : IdOf(reel),
                        Author = author.Length > 0 ? author : "instagram",
                        Nickname = user.ValueKind == JsonValueKind.Object ? Str(user, "full_name") : "",
                        Title = Str(reel, "title"), // highlights have a title; user stories don't
                        Items = items,
                        Referer = Referer,
                    };
                }
            }
        }
        return null;
    }

    private static void FindReels(JsonElement el, List<JsonElement> found, int depth)
    {
        if (depth > 40) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Name == "reels_media" && prop.Value.ValueKind == JsonValueKind.Array)
                        found.AddRange(prop.Value.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object));
                    else
                        FindReels(prop.Value, found, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) FindReels(item, found, depth + 1);
                break;
        }
    }

    private static List<MediaGalleryItem> ParseItems(JsonElement reel)
    {
        var items = new List<MediaGalleryItem>();
        if (!reel.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) return items;

        foreach (var it in arr.EnumerateArray())
            if (ParseMediaItem(it, items.Count + 1) is { } item) items.Add(item);
        return items;
    }

    /// <summary>
    /// One Instagram media item (story item, post, or carousel child) → gallery item, or null when it
    /// carries no downloadable URL. <c>media_type</c> 2 = video (<c>video_versions</c>), otherwise photo.
    /// </summary>
    internal static MediaGalleryItem? ParseMediaItem(JsonElement it, int index)
    {
        var images = Candidates(it, "image_versions2", "candidates");

        if (Int(it, "media_type") == 2)
        {
            var videos = it.TryGetProperty("video_versions", out var vv) && vv.ValueKind == JsonValueKind.Array
                ? vv.EnumerateArray()
                    .Select(v => (Url: Str(v, "url"), Area: Int(v, "width") * Int(v, "height")))
                    .Where(v => v.Url.Length > 0)
                    .OrderByDescending(v => v.Area)
                    .Select(v => v.Url).Distinct().ToList()
                : new List<string>();
            if (videos.Count == 0) return null;

            return new MediaGalleryItem
            {
                Index = index,
                IsVideo = true,
                Urls = videos,
                ThumbnailUrls = images.Select(i => i.Url).ToList(),
            };
        }

        if (images.Count == 0) return null;
        return new MediaGalleryItem
        {
            Index = index,
            Urls = images.Select(i => i.Url).ToList(),
            Width = images[0].Width,
            Height = images[0].Height,
        };
    }

    /// <summary>Image candidates, largest first.</summary>
    private static List<(string Url, int Width, int Height)> Candidates(JsonElement item, string outer, string inner)
    {
        if (!item.TryGetProperty(outer, out var o) || o.ValueKind != JsonValueKind.Object
            || !o.TryGetProperty(inner, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return new();
        }
        return list.EnumerateArray()
            .Select(c => (Url: Str(c, "url"), Width: Int(c, "width"), Height: Int(c, "height")))
            .Where(c => c.Url.Length > 0)
            .OrderByDescending(c => c.Width * c.Height)
            .ToList();
    }

    /// <summary>The element's id — Instagram sends it as a string in some payloads and a number in others.</summary>
    private static string IdOf(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "id", "pk" })
        {
            if (!e.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText();
        }
        return "";
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Reads an int that may arrive as a number or a numeric string.</summary>
    internal static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : 0;
    }
}

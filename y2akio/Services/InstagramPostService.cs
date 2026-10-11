using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// Photos (and videos) of an Instagram post (<c>instagram.com/p/&lt;shortcode&gt;/</c>), carousels included.
/// The post page embeds the media in its Relay payload
/// (<c>xdt_api__v1__media__shortcode__web_info.items[0]</c>); when it doesn't, the same item comes
/// from the web API <c>/api/v1/media/&lt;mediaId&gt;/info/</c> (media id decoded from the shortcode).
/// Item shape is the same as stories: <c>media_type</c> 1 photo, 2 video, 8 carousel (<c>carousel_media[]</c>).
/// </summary>
public class InstagramPostService
{
    private const string ShortcodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    /// <summary>True for <c>instagram.com/p/&lt;shortcode&gt;</c> (also <c>/&lt;user&gt;/p/&lt;shortcode&gt;</c>). Reels stay with yt-dlp.</summary>
    public static bool IsPostUrl(string url) => ShortcodeOf(url) is not null;

    private static string? ShortcodeOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !MediaGalleryService.IsHostOf(uri.Host, "instagram.com"))
            return null;
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindIndex(segs, s => s.Equals("p", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < segs.Length ? segs[i + 1] : null;
    }

    /// <param name="cookiesText">Netscape cookies.txt content; only instagram.com entries are sent.</param>
    public async Task<MediaGalleryPost> FetchPostAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var shortcode = ShortcodeOf(url) ?? throw new InvalidOperationException("This doesn't look like an Instagram post link.");
        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "instagram.com"));

        var (html, finalUri) = await MediaGalleryService.GetPageAsync($"https://www.instagram.com/p/{shortcode}/", cookieHeader, ct);
        if (finalUri.AbsolutePath.StartsWith("/accounts/login", StringComparison.OrdinalIgnoreCase)
            || finalUri.AbsolutePath.Contains("challenge", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(cookieHeader.Contains("sessionid=")
                ? "Instagram sent this request to the login page — the imported cookies have expired. Re-export cookies.txt from a logged-in browser and import it in Settings."
                : "Instagram only shows this post to logged-in users. Import your browser's cookies.txt (with a logged-in Instagram session) in Settings.");
        }

        // 1) Media embedded in the page.
        var item = FindPostItem(FacebookStoryService.JsonScriptRegex.Matches(html).Select(m => m.Groups[1].Value), shortcode);

        // 2) Web API fallback (needs a logged-in session).
        if (item is null && cookieHeader.Contains("sessionid="))
        {
            var json = await InstagramStoryService.GetApiAsync(
                $"https://www.instagram.com/api/v1/media/{MediaIdFromShortcode(shortcode)}/info/", cookieHeader, ct);
            item = FindPostItem(new[] { json }, shortcode);
        }

        if (item is null)
            throw new InvalidOperationException(
                "No media found in this post. It may be deleted, or private (only visible if you follow the account — import logged-in cookies in Settings).");
        return item;
    }

    /// <summary>Finds the post's media item (by shortcode) in the given JSON blobs and turns it into a gallery post.</summary>
    private static MediaGalleryPost? FindPostItem(IEnumerable<string> blobs, string shortcode)
    {
        foreach (var json in blobs)
        {
            if (!json.Contains(shortcode, StringComparison.Ordinal) || !json.Contains("image_versions2", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (FindByCode(doc.RootElement, shortcode, depth: 0) is not { } media) continue;

                var items = new List<MediaGalleryItem>();
                if (media.TryGetProperty("carousel_media", out var carousel) && carousel.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in carousel.EnumerateArray())
                        if (InstagramStoryService.ParseMediaItem(child, items.Count + 1) is { } it) items.Add(it);
                }
                else if (InstagramStoryService.ParseMediaItem(media, 1) is { } single)
                {
                    items.Add(single);
                }
                if (items.Count == 0) continue;

                var user = media.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
                var caption = media.TryGetProperty("caption", out var c) && c.ValueKind == JsonValueKind.Object
                    ? InstagramStoryService.Str(c, "text")
                    : "";
                var username = user.ValueKind == JsonValueKind.Object ? InstagramStoryService.Str(user, "username") : "";

                return new MediaGalleryPost
                {
                    Source = "Instagram Post",
                    Id = shortcode,
                    Author = username.Length > 0 ? username : "instagram",
                    Nickname = user.ValueKind == JsonValueKind.Object ? InstagramStoryService.Str(user, "full_name") : "",
                    Title = FirstLine(caption),
                    Items = items,
                    Referer = InstagramStoryService.Referer,
                };
            }
        }
        return null;
    }

    /// <summary>The media object whose <c>code</c> is the shortcode (carousel children have no code of their own).</summary>
    private static JsonElement? FindByCode(JsonElement el, string shortcode, int depth)
    {
        if (depth > 60) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (InstagramStoryService.Str(el, "code") == shortcode && el.TryGetProperty("image_versions2", out _))
                    return el.Clone();
                foreach (var prop in el.EnumerateObject())
                    if (FindByCode(prop.Value, shortcode, depth + 1) is { } r) return r;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (FindByCode(item, shortcode, depth + 1) is { } r) return r;
                break;
        }
        return null;
    }

    /// <summary>Shortcodes are the media id in base64url (A–Z a–z 0–9 - _) — private posts' longer codes carry extra trailing chars.</summary>
    private static string MediaIdFromShortcode(string shortcode)
    {
        var code = shortcode.Length > 11 ? shortcode[..11] : shortcode;
        BigInteger id = 0;
        foreach (var ch in code)
        {
            var v = ShortcodeAlphabet.IndexOf(ch);
            if (v < 0) break;
            id = id * 64 + v;
        }
        return id.ToString();
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}

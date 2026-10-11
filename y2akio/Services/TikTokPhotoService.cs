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
/// TikTok photo posts ("photo mode" / slideshows, <c>tiktok.com/@user/photo/&lt;id&gt;</c>). yt-dlp only
/// surfaces the background audio of these posts, never the images, so they are scraped directly:
/// the post page embeds its full item JSON in <c>__UNIVERSAL_DATA_FOR_REHYDRATION__</c>, whose
/// <c>imagePost.images[].imageURL.urlList</c> holds signed CDN links to the full-size images.
/// </summary>
public class TikTokPhotoService
{
    public const string Referer = "https://www.tiktok.com/";

    private static readonly Regex RehydrationRegex = new(
        @"<script[^>]+id=""__UNIVERSAL_DATA_FOR_REHYDRATION__""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SigiRegex = new(
        @"<script[^>]+id=""SIGI_STATE""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex PostIdRegex = new(@"/(?:photo|video)/(\d+)", RegexOptions.Compiled);

    /// <summary>True for a direct <c>tiktok.com/.../photo/&lt;id&gt;</c> link.</summary>
    public static bool IsPhotoUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && MediaGalleryService.IsHostOf(uri.Host, "tiktok.com")
        && uri.AbsolutePath.Contains("/photo/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for share short links (vt./vm.tiktok.com, tiktok.com/t/...) whose target is unknown until resolved.</summary>
    public static bool IsShortLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("vt.tiktok.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("vm.tiktok.com", StringComparison.OrdinalIgnoreCase)
            || (MediaGalleryService.IsHostOf(uri.Host, "tiktok.com") && uri.AbsolutePath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Follows a share short link's redirects and returns the final URL (or the input on failure).</summary>
    public async Task<string> ResolveShortLinkAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await MediaGalleryService.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return response.RequestMessage?.RequestUri?.ToString() ?? url;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Loads the post's metadata + image list. The <c>/photo/</c> page itself ships without the item
    /// data, but the same ID under <c>/video/</c> renders the full item (imagePost included), so that
    /// form is tried first, then the original URL as a fallback.
    /// </summary>
    /// <param name="cookiesText">Netscape cookies.txt content (only tiktok.com entries are sent). Null/empty = anonymous.</param>
    public async Task<MediaGalleryPost> FetchPostAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var idMatch = PostIdRegex.Match(url);
        if (!idMatch.Success)
            throw new InvalidOperationException("This doesn't look like a TikTok photo post link.");

        var postId = idMatch.Groups[1].Value;
        var user = ExtractUser(url);
        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "tiktok.com"));

        var candidates = new List<string> { $"https://www.tiktok.com/@{user}/video/{postId}" };
        if (!candidates.Contains(url)) candidates.Add(url);

        // Private / friends-only posts only render for a logged-in viewer who is allowed to see them.
        var loggedIn = cookieHeader.Contains("sessionid=");
        string lastError = loggedIn
            ? "TikTok returned no data for this post. It may be deleted, region-locked, or private / friends-only and not visible to the account in your imported cookies."
            : "TikTok returned no data for this post. If it's private or friends-only, import your browser's cookies.txt (with a logged-in TikTok session that can see it) in Settings.";
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (html, _) = await MediaGalleryService.GetPageAsync(candidate, cookieHeader, ct);
                var item = FindItemStruct(html);
                if (item is null) continue;

                var post = ParsePost(item.Value, postId, user);
                if (post.Items.Count > 0) return post;
                lastError = "This TikTok post has no photos (it's a regular video — paste it as a normal link).";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex is TikTokStatusException status ? status.Describe(loggedIn) : ex.Message;
            }
        }

        throw new InvalidOperationException(lastError);
    }

    // ─── Page parse ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pulls the item object out of either page-data format TikTok has shipped
    /// (<c>__UNIVERSAL_DATA_FOR_REHYDRATION__</c> now, <c>SIGI_STATE</c> on older builds) by searching
    /// for the first object carrying an <c>imagePost</c>. Also surfaces TikTok's own status message.
    /// </summary>
    private static JsonElement? FindItemStruct(string html)
    {
        foreach (var regex in new[] { RehydrationRegex, SigiRegex })
        {
            var match = regex.Match(html);
            if (!match.Success) continue;

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var found = FindObjectWithProperty(doc.RootElement, "imagePost", depth: 0);
            if (found is not null) return found.Value.Clone();

            if (doc.RootElement.TryGetProperty("__DEFAULT_SCOPE__", out var scope)
                && scope.TryGetProperty("webapp.video-detail", out var detail)
                && detail.TryGetProperty("statusCode", out var code)
                && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            {
                var msg = detail.TryGetProperty("statusMsg", out var m) ? m.GetString() : null;
                throw new TikTokStatusException(code.GetInt32(), msg);
            }
        }
        return null;
    }

    private static JsonElement? FindObjectWithProperty(JsonElement el, string name, int depth)
    {
        if (depth > 12) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object) return el;
                foreach (var prop in el.EnumerateObject())
                {
                    var r = FindObjectWithProperty(prop.Value, name, depth + 1);
                    if (r is not null) return r;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var r = FindObjectWithProperty(item, name, depth + 1);
                    if (r is not null) return r;
                }
                break;
        }
        return null;
    }

    private static MediaGalleryPost ParsePost(JsonElement item, string postId, string userFromUrl)
    {
        string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        var author = userFromUrl;
        var nickname = "";
        if (item.TryGetProperty("author", out var authorEl))
        {
            if (authorEl.ValueKind == JsonValueKind.Object)
            {
                author = Str(authorEl, "uniqueId") is { Length: > 0 } u ? u : author;
                nickname = Str(authorEl, "nickname");
            }
            else if (authorEl.ValueKind == JsonValueKind.String)
            {
                author = authorEl.GetString() ?? author; // SIGI_STATE stores the handle directly
            }
        }

        var imagePost = item.GetProperty("imagePost");
        var title = Str(imagePost, "title");
        var desc = Str(item, "desc");

        var images = new List<MediaGalleryItem>();
        if (imagePost.TryGetProperty("images", out var imagesEl) && imagesEl.ValueKind == JsonValueKind.Array)
        {
            var index = 1;
            foreach (var img in imagesEl.EnumerateArray())
            {
                var urls = new List<string>();
                if (img.TryGetProperty("imageURL", out var urlEl)
                    && urlEl.TryGetProperty("urlList", out var listEl)
                    && listEl.ValueKind == JsonValueKind.Array)
                {
                    urls.AddRange(listEl.EnumerateArray()
                        .Where(u => u.ValueKind == JsonValueKind.String)
                        .Select(u => u.GetString()!)
                        .Where(u => !string.IsNullOrWhiteSpace(u)));
                }
                if (urls.Count == 0) continue;

                // JPEG mirrors first: WinUI's BitmapImage can't decode HEIC without the extension pack.
                urls = urls.OrderBy(u => u.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
                                         || u.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

                images.Add(new MediaGalleryItem
                {
                    Index = index++,
                    Urls = urls,
                    Width = img.TryGetProperty("imageWidth", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
                    Height = img.TryGetProperty("imageHeight", out var h) && h.TryGetInt32(out var hi) ? hi : 0,
                });
            }
        }

        return new MediaGalleryPost
        {
            Source = "TikTok",
            Id = Str(item, "id") is { Length: > 0 } id ? id : postId,
            Author = string.IsNullOrWhiteSpace(author) ? "tiktok" : author,
            Nickname = nickname,
            Title = !string.IsNullOrWhiteSpace(title) ? title : desc,
            Items = images,
            Referer = TikTokPhotoService.Referer,
        };
    }

    private static string ExtractUser(string url)
    {
        var m = Regex.Match(url, @"/@([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "tiktok";
    }
}

/// <summary>TikTok's own non-zero <c>statusCode</c> for an item (private, deleted, ...), turned into a readable message.</summary>
internal sealed class TikTokStatusException : Exception
{
    public int Code { get; }
    public string? StatusMessage { get; }

    public TikTokStatusException(int code, string? statusMessage)
        : base($"TikTok refused the request (status {code}{(string.IsNullOrWhiteSpace(statusMessage) ? "" : $": {statusMessage}")}).")
    {
        Code = code;
        StatusMessage = statusMessage;
    }

    /// <summary>
    /// Readable explanation. 10216/10222 are TikTok's "private / no permission" codes, 10204 is
    /// "item doesn't exist"; anything else keeps the raw status.
    /// </summary>
    public string Describe(bool loggedIn) => Code switch
    {
        10216 or 10222 when loggedIn =>
            "This TikTok post is private or friends-only, and the account in your imported cookies isn't allowed to see it.",
        10216 or 10222 =>
            "This TikTok post is private or friends-only. Import your browser's cookies.txt (with a logged-in TikTok session that can see it) in Settings.",
        10204 => "This TikTok post doesn't exist anymore (deleted, or the link is wrong).",
        _ => Message,
    };
}

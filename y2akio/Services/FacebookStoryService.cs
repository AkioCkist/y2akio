using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// Facebook stories (<c>facebook.com/stories/&lt;bucketId&gt;/&lt;storyId&gt;/</c>). yt-dlp's facebook extractor
/// doesn't support story links, and stories are login-only, so the story page is fetched with the
/// imported cookies and its Relay payload parsed directly: the page's <c>application/json</c> scripts
/// carry the bucket (<c>story_bucket_owner</c> + <c>unified_stories_with_notes.edges[].node</c>) whose
/// <c>attachments[].media</c> is a <c>Video</c> (progressive HD/SD mp4 links, audio included) or a
/// <c>Photo</c> (<c>image.uri</c>).
/// </summary>
public class FacebookStoryService
{
    internal static readonly Regex JsonScriptRegex = new(
        @"<script type=""application/json""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>True for <c>facebook.com/stories/...</c> links (www., m., web. hosts included).</summary>
    public static bool IsStoryUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && MediaGalleryService.IsHostOf(uri.Host, "facebook.com")
        && uri.AbsolutePath.StartsWith("/stories/", StringComparison.OrdinalIgnoreCase);

    /// <param name="cookiesText">Netscape cookies.txt content; only facebook.com entries are sent. Stories need a logged-in session.</param>
    public async Task<MediaGalleryPost> FetchStoryAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var uri = new Uri(url);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var bucketId = segments.Length > 1 ? segments[1] : "";
        var storyId = segments.Length > 2 ? Uri.UnescapeDataString(segments[2]) : "";

        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "facebook.com"));
        if (!cookieHeader.Contains("c_user="))
            throw new InvalidOperationException(
                "Facebook stories are only visible when logged in. Import your browser's cookies.txt (with a logged-in Facebook session) in Settings.");

        // The mobile/basic hosts render a different page; the www page carries the Relay payload.
        var pageUrl = $"https://www.facebook.com{uri.AbsolutePath}{uri.Query}";
        var (html, finalUri) = await MediaGalleryService.GetPageAsync(pageUrl, cookieHeader, ct);
        if (finalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
            || finalUri.AbsolutePath.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Facebook sent this request to the login page — the imported cookies have expired. Re-export cookies.txt from a logged-in browser and import it in Settings.");
        }

        var post = ParseBucket(html, bucketId, storyId);
        if (post is null || post.Items.Count == 0)
            throw new InvalidOperationException(
                "No story media found. The story may have expired (stories last 24h), been deleted, or not be shared with your account.");
        return post;
    }

    // ─── Page parse ──────────────────────────────────────────────────────────

    /// <summary>
    /// Collects every populated story node of the target bucket across all JSON blobs on the page
    /// (the same bucket shows up several times, often as id-only stubs). The bucket is matched by the
    /// id in the URL; if none matches, any bucket containing the linked story id is used.
    /// </summary>
    private static MediaGalleryPost? ParseBucket(string html, string bucketId, string storyId)
    {
        var items = new List<MediaGalleryItem>();
        var seenMedia = new HashSet<string>();
        string ownerName = "", ownerSlug = "";

        foreach (Match m in JsonScriptRegex.Matches(html))
        {
            var json = m.Groups[1].Value;
            if (!json.Contains("unified_stories", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var buckets = new List<JsonElement>();
                FindBuckets(doc.RootElement, buckets, depth: 0);

                foreach (var bucket in buckets)
                {
                    var nodes = StoryNodes(bucket).ToList();
                    var isTarget = Str(bucket, "id") == bucketId
                                   || (!string.IsNullOrEmpty(storyId) && nodes.Any(n => Str(n, "id") == storyId));
                    if (!isTarget) continue;

                    if (string.IsNullOrEmpty(ownerName)
                        && bucket.TryGetProperty("story_bucket_owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
                    {
                        ownerName = Str(owner, "name");
                        ownerSlug = SlugFromProfileUrl(Str(owner, "url")) is { Length: > 0 } slug ? slug : Str(owner, "id");
                    }

                    foreach (var node in nodes)
                    {
                        if (!node.TryGetProperty("attachments", out var atts) || atts.ValueKind != JsonValueKind.Array) continue;
                        foreach (var att in atts.EnumerateArray())
                        {
                            if (!att.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object) continue;
                            var mediaId = Str(media, "id");
                            if (mediaId.Length > 0 && seenMedia.Contains(mediaId)) continue;

                            var item = ParseMedia(media, items.Count + 1);
                            if (item is null) continue; // id-only stub — the full copy appears elsewhere on the page
                            if (mediaId.Length > 0) seenMedia.Add(mediaId);
                            items.Add(item);
                        }
                    }
                }
            }
        }

        if (items.Count == 0) return null;
        return new MediaGalleryPost
        {
            Source = "Facebook Story",
            Id = bucketId,
            Author = string.IsNullOrWhiteSpace(ownerSlug) ? "facebook" : ownerSlug,
            Title = ownerName,
            Items = items,
        };
    }

    private static void FindBuckets(JsonElement el, List<JsonElement> found, int depth)
    {
        if (depth > 40) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("unified_stories_with_notes", out _) || el.TryGetProperty("unified_stories", out _))
                    found.Add(el);
                foreach (var prop in el.EnumerateObject()) FindBuckets(prop.Value, found, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) FindBuckets(item, found, depth + 1);
                break;
        }
    }

    private static IEnumerable<JsonElement> StoryNodes(JsonElement bucket)
    {
        foreach (var key in new[] { "unified_stories_with_notes", "unified_stories" })
        {
            if (!bucket.TryGetProperty(key, out var stories) || stories.ValueKind != JsonValueKind.Object) continue;
            if (!stories.TryGetProperty("edges", out var edges) || edges.ValueKind != JsonValueKind.Array) continue;
            foreach (var edge in edges.EnumerateArray())
                if (edge.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object)
                    yield return node;
        }
    }

    /// <summary>Turns one attachment's media into a gallery item, or null when it carries no downloadable URL.</summary>
    internal static MediaGalleryItem? ParseMedia(JsonElement media, int index)
    {
        var type = Str(media, "__typename");
        if (type == "Video")
        {
            var urls = new List<string>();
            // Progressive mp4s (audio muxed in), HD first.
            if (media.TryGetProperty("videoDeliveryResponseFragment", out var frag)
                && frag.TryGetProperty("videoDeliveryResponseResult", out var result)
                && result.TryGetProperty("progressive_urls", out var progressive)
                && progressive.ValueKind == JsonValueKind.Array)
            {
                var entries = progressive.EnumerateArray()
                    .Select(p => (Url: Str(p, "progressive_url"),
                                  Hd: p.TryGetProperty("metadata", out var md) && Str(md, "quality") == "HD"))
                    .Where(p => p.Url.Length > 0)
                    .OrderByDescending(p => p.Hd);
                urls.AddRange(entries.Select(p => p.Url));
            }
            foreach (var key in new[] { "browser_native_hd_url", "playable_url_quality_hd", "browser_native_sd_url", "playable_url" })
                if (Str(media, key) is { Length: > 0 } u) urls.Add(u);
            urls = urls.Distinct().ToList();
            if (urls.Count == 0) return null;

            return new MediaGalleryItem
            {
                Index = index,
                IsVideo = true,
                Urls = urls,
                ThumbnailUrls = ImageUris(media, "preferred_thumbnail.image", "previewImage", "image", "blurred_image"),
            };
        }

        if (type == "Photo")
        {
            var urls = ImageUris(media, "viewer_image", "photo_image", "image", "previewImage");
            if (urls.Count == 0) return null;

            // Size of the largest rendition present (viewer_image is the full-size one).
            var (w, h) = (0, 0);
            foreach (var key in new[] { "viewer_image", "photo_image", "image" })
            {
                if (media.TryGetProperty(key, out var img) && img.ValueKind == JsonValueKind.Object && Int(img, "width") > 0)
                {
                    (w, h) = (Int(img, "width"), Int(img, "height"));
                    break;
                }
            }
            return new MediaGalleryItem { Index = index, Urls = urls, Width = w, Height = h };
        }

        return null;
    }

    /// <summary>The <c>.uri</c> of each named (dot-path) image object that has one, in the given order.</summary>
    internal static List<string> ImageUris(JsonElement media, params string[] paths)
    {
        var uris = new List<string>();
        foreach (var path in paths)
        {
            var el = media;
            var ok = true;
            foreach (var part in path.Split('.'))
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(part, out el)) { ok = false; break; }
            }
            if (ok && el.ValueKind == JsonValueKind.Object && Str(el, "uri") is { Length: > 0 } uri && !uris.Contains(uri))
                uris.Add(uri);
        }
        return uris;
    }

    /// <summary>"https://www.facebook.com/pvtd264" → "pvtd264"; profile.php?id=… → the id.</summary>
    internal static string SlugFromProfileUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var idMatch = Regex.Match(uri.Query, @"[?&]id=(\d+)");
        if (idMatch.Success) return idMatch.Groups[1].Value;
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segs.Length > 0 && segs[0] != "profile.php" ? segs[0] : "";
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    internal static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : 0;
    }
}

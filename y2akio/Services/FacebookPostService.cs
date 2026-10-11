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
/// Photos of a Facebook post (share/p links, permalinks, /posts/, group posts, single /photo pages).
/// The post page's Relay payload carries the attachment: an album
/// (<c>styles.attachment.mediaset_token</c> + <c>all_subattachments.nodes[].media</c>) or a single
/// photo (<c>styles.attachment.media</c>). An album only embeds its first few photos at full size
/// (<c>viewer_image</c>), so the full list comes from the album page (<c>/media/set/?set=&lt;token&gt;</c>,
/// preview-sized), and each missing photo's full-size image is looked up from its own photo page
/// (<c>currMedia.image</c>) only when it is actually downloaded.
/// </summary>
public class FacebookPostService
{
    private static readonly Regex PhotoIdRegex = new(@"[?&]fbid=(\d+)", RegexOptions.Compiled);

    /// <summary>
    /// True for Facebook post / photo links. Video and reel links (share/v, share/r, /watch, /reel)
    /// are left to yt-dlp.
    /// </summary>
    public static bool IsPostUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !MediaGalleryService.IsHostOf(uri.Host, "facebook.com"))
            return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.StartsWith("/share/p/")
            || path.StartsWith("/permalink.php")
            || path.StartsWith("/story.php")
            || path.Contains("/posts/")
            || (path.StartsWith("/groups/") && path.Contains("/permalink/"))
            || path.StartsWith("/photo")          // /photo/?fbid=…, /photo.php?fbid=…
            || path.Contains("/photos/");         // /<page>/photos/a.…/<id>/
    }

    /// <param name="cookiesText">Netscape cookies.txt content; only facebook.com entries are sent. Most posts need a logged-in session.</param>
    /// <returns>The post's photos, or null when it has none (video posts etc. are left to yt-dlp).</returns>
    public async Task<MediaGalleryPost?> FetchPostAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var cookieHeader = MediaGalleryService.BuildCookieHeader(cookiesText, d => MediaGalleryService.IsHostOf(d, "facebook.com"));

        var uri = new Uri(url);
        var pageUrl = $"https://www.facebook.com{uri.AbsolutePath}{uri.Query}";
        var (html, finalUri) = await MediaGalleryService.GetPageAsync(pageUrl, cookieHeader, ct);
        if (finalUri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
            || finalUri.AbsolutePath.Contains("checkpoint", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.IsNullOrEmpty(cookieHeader)
                ? "Facebook only shows this post to logged-in users. Import your browser's cookies.txt (with a logged-in Facebook session) in Settings."
                : "Facebook sent this request to the login page — the imported cookies have expired. Re-export cookies.txt from a logged-in browser and import it in Settings.");
        }

        var blobs = FacebookStoryService.JsonScriptRegex.Matches(html).Select(m => m.Groups[1].Value).ToList();

        // A /photo page: the photo itself is "currMedia".
        var path = finalUri.AbsolutePath.ToLowerInvariant();
        if ((path.StartsWith("/photo") || path.Contains("/photos/")) && ParsePhotoPage(blobs, finalUri) is { } photoPage)
            return photoPage;

        // No photo attachment (video post, text-only, ...): not a gallery post — the caller hands it to yt-dlp.
        var post = ParsePost(blobs);
        if (post is null) return null;

        // Albums: fill in the photos the post page didn't embed.
        if (post.MediasetToken is { Length: > 0 } token && post.TotalCount > post.Items.Count)
            await AddAlbumRestAsync(post, token, cookieHeader, ct);

        return post.ToGalleryPost();
    }

    // ─── Post page ───────────────────────────────────────────────────────────

    private sealed class ParsedPost
    {
        public string Id = "", Author = "", Name = "", Message = "", MediasetToken = "";
        public int TotalCount;
        public List<(string MediaId, MediaGalleryItem Item)> Items = new();

        public MediaGalleryPost ToGalleryPost() => new()
        {
            Source = "Facebook Post",
            Id = Id,
            Author = string.IsNullOrWhiteSpace(Author) ? "facebook" : Author,
            Nickname = Name,
            Title = FirstLine(Message) is { Length: > 0 } line ? line : Name,
            Items = Items.Select((x, i) => Renumber(x.Item, i + 1)).ToList(),
        };
    }

    /// <summary>
    /// The requested post is the page's first story (feed and sidebar units load after it, in later
    /// JSON blobs).
    /// </summary>
    private static ParsedPost? ParsePost(List<string> blobs)
    {
        foreach (var json in blobs)
        {
            if (!json.Contains("\"attachments\"", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var stories = new List<JsonElement>();
                FindStories(doc.RootElement, stories, depth: 0);
                if (stories.Count == 0) continue;

                // Only the first blob with stories belongs to the requested post (outer feed unit + its
                // content story). Later blobs are the news feed / sidebar — never fall through to them,
                // or a text/video post would pick up some other post's photos.
                foreach (var story in stories)
                    if (ParseStory(story) is { } parsed) return parsed;
                return null;
            }
        }
        return null;
    }

    /// <summary>Story objects: carry both <c>attachments</c> and <c>actors</c> (the post author).</summary>
    private static void FindStories(JsonElement el, List<JsonElement> found, int depth)
    {
        if (depth > 60) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array
                    && el.TryGetProperty("actors", out var act) && act.ValueKind == JsonValueKind.Array)
                {
                    found.Add(el);
                }
                foreach (var prop in el.EnumerateObject()) FindStories(prop.Value, found, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) FindStories(item, found, depth + 1);
                break;
        }
    }

    private static ParsedPost? ParseStory(JsonElement story)
    {
        var post = new ParsedPost();
        foreach (var att in story.GetProperty("attachments").EnumerateArray())
        {
            if (!att.TryGetProperty("styles", out var styles) || styles.ValueKind != JsonValueKind.Object
                || !styles.TryGetProperty("attachment", out var attachment) || attachment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Album.
            if (attachment.TryGetProperty("all_subattachments", out var subs) && subs.ValueKind == JsonValueKind.Object)
            {
                post.MediasetToken = FacebookStoryService.Str(attachment, "mediaset_token");
                post.TotalCount = FacebookStoryService.Int(subs, "count");
                if (subs.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var node in nodes.EnumerateArray())
                    {
                        if (!node.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object) continue;
                        if (FacebookStoryService.ParseMedia(media, post.Items.Count + 1) is { } item)
                            post.Items.Add((FacebookStoryService.Str(media, "id"), item));
                    }
                }
            }
            // Single photo.
            else if (attachment.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object
                     && FacebookStoryService.ParseMedia(media, post.Items.Count + 1) is { } item)
            {
                post.Items.Add((FacebookStoryService.Str(media, "id"), item));
            }
        }

        // A lone video attachment is a video post — yt-dlp handles those with full quality options.
        if (string.IsNullOrEmpty(post.MediasetToken) && !post.Items.Any(x => !x.Item.IsVideo)) return null;

        post.Id = FacebookStoryService.Str(story, "post_id");
        // The caption sits on the story itself, or (for the outer feed unit) on its rendered content story.
        var captionHolder = story;
        if (!story.TryGetProperty("message", out _)
            && story.TryGetProperty("comet_sections", out var cs) && cs.ValueKind == JsonValueKind.Object
            && cs.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("story", out var inner) && inner.ValueKind == JsonValueKind.Object)
        {
            captionHolder = inner;
        }
        if (captionHolder.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            post.Message = FacebookStoryService.Str(msg, "text");

        var actor = story.GetProperty("actors").EnumerateArray().FirstOrDefault();
        if (actor.ValueKind == JsonValueKind.Object)
        {
            post.Name = FacebookStoryService.Str(actor, "name");
            post.Author = FacebookStoryService.SlugFromProfileUrl(FacebookStoryService.Str(actor, "url")) is { Length: > 0 } slug
                ? slug
                : FacebookStoryService.Str(actor, "id");
        }
        if (string.IsNullOrEmpty(post.Id) && post.MediasetToken.StartsWith("pcb."))
            post.Id = post.MediasetToken["pcb.".Length..];
        return post;
    }

    // ─── Album page ──────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the album page for the full photo list (in album order). Photos already embedded at full
    /// size keep their data; the rest get their preview as tile + fallback and a lazy lookup of the
    /// full-size image from their own photo page.
    /// </summary>
    private static async Task AddAlbumRestAsync(ParsedPost post, string token, string cookieHeader, CancellationToken ct)
    {
        string html;
        try
        {
            (html, _) = await MediaGalleryService.GetPageAsync(
                $"https://www.facebook.com/media/set/?set={Uri.EscapeDataString(token)}&type=3", cookieHeader, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return; // keep what the post page had
        }

        var known = post.Items.ToDictionary(x => x.MediaId, x => x.Item);
        var ordered = new List<(string, MediaGalleryItem)>();
        var seen = new HashSet<string>();

        foreach (var json in FacebookStoryService.JsonScriptRegex.Matches(html).Select(m => m.Groups[1].Value))
        {
            if (!json.Contains("\"album\"", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var edges = new List<JsonElement>();
                FindAlbumEdges(doc.RootElement, edges, depth: 0);
                foreach (var node in edges)
                {
                    var id = FacebookStoryService.Str(node, "id");
                    if (id.Length == 0 || !seen.Add(id)) continue;

                    if (known.TryGetValue(id, out var full))
                    {
                        ordered.Add((id, full));
                        continue;
                    }

                    if (FacebookStoryService.Str(node, "__typename") != "Photo") continue; // album videos: open them as normal links
                    var preview = FacebookStoryService.ImageUris(node, "image");
                    if (preview.Count == 0) continue;

                    var photoId = id;
                    ordered.Add((id, new MediaGalleryItem
                    {
                        Urls = preview.ToList(),
                        ThumbnailUrls = preview,
                        ResolveFullUrlsAsync = c => ResolvePhotoAsync(photoId, token, cookieHeader, c),
                    }));
                }
            }
        }

        // Anything embedded in the post but missing from the album page stays, appended.
        foreach (var (id, item) in post.Items)
            if (!seen.Contains(id)) ordered.Add((id, item));

        if (ordered.Count >= post.Items.Count) post.Items = ordered;
    }

    private static void FindAlbumEdges(JsonElement el, List<JsonElement> nodes, int depth)
    {
        if (depth > 60) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("album", out var album) && album.ValueKind == JsonValueKind.Object
                    && album.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object
                    && media.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                {
                    foreach (var edge in edges.EnumerateArray())
                        if (edge.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object)
                            nodes.Add(node);
                }
                foreach (var prop in el.EnumerateObject()) FindAlbumEdges(prop.Value, nodes, depth + 1);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) FindAlbumEdges(item, nodes, depth + 1);
                break;
        }
    }

    // ─── Photo page ──────────────────────────────────────────────────────────

    /// <summary>Full-size image URL of one photo, read from its photo page.</summary>
    private static async Task<List<string>> ResolvePhotoAsync(string photoId, string setToken, string cookieHeader, CancellationToken ct)
    {
        var (html, _) = await MediaGalleryService.GetPageAsync(
            $"https://www.facebook.com/photo/?fbid={photoId}&set={Uri.EscapeDataString(setToken)}", cookieHeader, ct);
        var blobs = FacebookStoryService.JsonScriptRegex.Matches(html).Select(m => m.Groups[1].Value);
        return FindCurrMedia(blobs, photoId) is { } media
            ? FacebookStoryService.ImageUris(media, "image", "viewer_image", "photo_image")
            : new List<string>();
    }

    /// <summary>A /photo page opened directly: one item, the page's current photo.</summary>
    private static MediaGalleryPost? ParsePhotoPage(List<string> blobs, Uri finalUri)
    {
        var idMatch = PhotoIdRegex.Match(finalUri.Query);
        var photoId = idMatch.Success ? idMatch.Groups[1].Value : "";
        if (FindCurrMedia(blobs, photoId) is not { } media) return null;

        var urls = FacebookStoryService.ImageUris(media, "image", "viewer_image", "photo_image");
        if (urls.Count == 0) return null;

        var owner = media.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        var ownerId = owner.ValueKind == JsonValueKind.Object ? FacebookStoryService.Str(owner, "id") : "";
        var size = media.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? img : default;
        return new MediaGalleryPost
        {
            Source = "Facebook Photo",
            Id = FacebookStoryService.Str(media, "id"),
            Author = ownerId.Length > 0 ? ownerId : "facebook",
            Nickname = owner.ValueKind == JsonValueKind.Object ? FacebookStoryService.Str(owner, "name") : "",
            Items = new()
            {
                new MediaGalleryItem
                {
                    Index = 1,
                    Urls = urls,
                    Width = size.ValueKind == JsonValueKind.Object ? FacebookStoryService.Int(size, "width") : 0,
                    Height = size.ValueKind == JsonValueKind.Object ? FacebookStoryService.Int(size, "height") : 0,
                },
            },
        };
    }

    /// <summary>The <c>currMedia</c> object of a photo page (matching <paramref name="photoId"/> when given).</summary>
    private static JsonElement? FindCurrMedia(IEnumerable<string> blobs, string photoId)
    {
        foreach (var json in blobs)
        {
            if (!json.Contains("\"currMedia\"", StringComparison.Ordinal)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (FindProperty(doc.RootElement, "currMedia", depth: 0) is { } media
                    && (photoId.Length == 0 || FacebookStoryService.Str(media, "id") == photoId))
                {
                    return media.Clone();
                }
            }
        }
        return null;
    }

    private static JsonElement? FindProperty(JsonElement el, string name, int depth)
    {
        if (depth > 60) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object) return v;
                foreach (var prop in el.EnumerateObject())
                    if (FindProperty(prop.Value, name, depth + 1) is { } r) return r;
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    if (FindProperty(item, name, depth + 1) is { } r) return r;
                break;
        }
        return null;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Copies an item with a new 1-based position (items are numbered after the album order is final).</summary>
    private static MediaGalleryItem Renumber(MediaGalleryItem item, int index) => new()
    {
        Index = index,
        IsVideo = item.IsVideo,
        Urls = item.Urls,
        ThumbnailUrls = item.ThumbnailUrls,
        Width = item.Width,
        Height = item.Height,
        ResolveFullUrlsAsync = item.ResolveFullUrlsAsync,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}

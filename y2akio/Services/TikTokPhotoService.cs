using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    // UseCookies=false: the imported jar is sent as a hand-built Cookie header (tiktok.com only),
    // so it never leaks to the CDN hosts the images are fetched from.
    private static readonly HttpClient _http = CreateClient();

    private static readonly Regex RehydrationRegex = new(
        @"<script[^>]+id=""__UNIVERSAL_DATA_FOR_REHYDRATION__""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SigiRegex = new(
        @"<script[^>]+id=""SIGI_STATE""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex PostIdRegex = new(@"/(?:photo|video)/(\d+)", RegexOptions.Compiled);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>True for a direct <c>tiktok.com/.../photo/&lt;id&gt;</c> link.</summary>
    public static bool IsPhotoUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && IsTikTokHost(uri.Host)
        && uri.AbsolutePath.Contains("/photo/", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for share short links (vt./vm.tiktok.com, tiktok.com/t/...) whose target is unknown until resolved.</summary>
    public static bool IsShortLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("vt.tiktok.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("vm.tiktok.com", StringComparison.OrdinalIgnoreCase)
            || (IsTikTokHost(uri.Host) && uri.AbsolutePath.StartsWith("/t/", StringComparison.OrdinalIgnoreCase)));

    private static bool IsTikTokHost(string host) =>
        host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".tiktok.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Follows a share short link's redirects and returns the final URL (or the input on failure).</summary>
    public async Task<string> ResolveShortLinkAsync(string url, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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
    public async Task<TikTokPhotoPost> FetchPostAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var idMatch = PostIdRegex.Match(url);
        if (!idMatch.Success)
            throw new InvalidOperationException("This doesn't look like a TikTok photo post link.");

        var postId = idMatch.Groups[1].Value;
        var user = ExtractUser(url);
        var cookieHeader = BuildCookieHeader(cookiesText);

        var candidates = new List<string> { $"https://www.tiktok.com/@{user}/video/{postId}" };
        if (!candidates.Contains(url)) candidates.Add(url);

        string lastError = "TikTok returned no data for this post (it may be private, deleted, or region-locked).";
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var html = await GetPageAsync(candidate, cookieHeader, ct);
                var item = FindItemStruct(html);
                if (item is null) continue;

                var post = ParsePost(item.Value, postId, user);
                if (post.Images.Count > 0) return post;
                lastError = "This TikTok post has no photos (it's a regular video — paste it as a normal link).";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }
        }

        throw new InvalidOperationException(lastError);
    }

    /// <summary>Downloads one image's bytes, trying each CDN mirror in its url list until one succeeds.</summary>
    public async Task<byte[]> DownloadImageAsync(TikTokPhotoItem image, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in image.Urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = new Uri("https://www.tiktok.com/");
                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException($"Couldn't download photo {image.Index}: {last?.Message ?? "no URL"}");
    }

    /// <summary>Writes the given images as loose files into <paramref name="folder"/>. Returns the written paths.</summary>
    public static async Task<List<string>> SaveToFolderAsync(
        TikTokPhotoPost post, IReadOnlyList<TikTokPhotoItem> images, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            var path = UniquePath(Path.Combine(folder, FileNameFor(post, image)));
            await File.WriteAllBytesAsync(path, image.Bytes!, ct);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Packs the given images into a single .zip at <paramref name="zipPath"/> (overwrites nothing — picks a free name).</summary>
    public static async Task<string> SaveAsZipAsync(
        TikTokPhotoPost post, IReadOnlyList<TikTokPhotoItem> images, string zipPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        zipPath = UniquePath(zipPath);

        await using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var image in images)
        {
            ct.ThrowIfCancellationRequested();
            // Images are already compressed — Store avoids burning CPU for ~0% gain.
            var entry = archive.CreateEntry(FileNameFor(post, image), CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(image.Bytes!, ct);
        }
        return zipPath;
    }

    /// <summary>"author_postId" — used for the per-post subfolder and the zip name.</summary>
    public static string BaseNameFor(TikTokPhotoPost post) => SanitizeFileName($"{post.Author}_{post.Id}");

    private static string FileNameFor(TikTokPhotoPost post, TikTokPhotoItem image) =>
        $"{BaseNameFor(post)}_{image.Index:D2}.{image.Extension}";

    // ─── Page fetch + parse ──────────────────────────────────────────────────

    private static async Task<string> GetPageAsync(string url, string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        if (!string.IsNullOrEmpty(cookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

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
                throw new InvalidOperationException(
                    $"TikTok refused the request (status {code.GetInt32()}{(string.IsNullOrWhiteSpace(msg) ? "" : $": {msg}")}).");
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

    private static TikTokPhotoPost ParsePost(JsonElement item, string postId, string userFromUrl)
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

        var images = new List<TikTokPhotoItem>();
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

                images.Add(new TikTokPhotoItem
                {
                    Index = index++,
                    Urls = urls,
                    Width = img.TryGetProperty("imageWidth", out var w) && w.TryGetInt32(out var wi) ? wi : 0,
                    Height = img.TryGetProperty("imageHeight", out var h) && h.TryGetInt32(out var hi) ? hi : 0,
                });
            }
        }

        return new TikTokPhotoPost
        {
            Id = Str(item, "id") is { Length: > 0 } id ? id : postId,
            Author = string.IsNullOrWhiteSpace(author) ? "tiktok" : author,
            Nickname = nickname,
            Title = !string.IsNullOrWhiteSpace(title) ? title : desc,
            Images = images,
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string ExtractUser(string url)
    {
        var m = Regex.Match(url, @"/@([^/?#]+)");
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : "tiktok";
    }

    /// <summary>Builds a Cookie header from the tiktok.com entries of a Netscape cookies.txt (expired ones skipped).</summary>
    private static string BuildCookieHeader(string? cookiesText)
    {
        if (string.IsNullOrWhiteSpace(cookiesText)) return "";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in cookiesText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("#HttpOnly_", StringComparison.Ordinal)) line = line["#HttpOnly_".Length..];
            else if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split('\t');
            if (parts.Length < 7) continue;
            if (!IsTikTokHost(parts[0].TrimStart('.'))) continue;
            if (long.TryParse(parts[4], out var expiry) && expiry > 0 && expiry < now) continue;

            var name = parts[5];
            var value = parts[6];
            // Header values must stay single-line ASCII; drop anything that would make the header invalid.
            if (name.Length == 0 || (name + value).Any(c => c < 0x20 || c > 0x7E || c == ';')) continue;
            pairs[name] = value;
        }
        return string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim().TrimEnd('.');
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

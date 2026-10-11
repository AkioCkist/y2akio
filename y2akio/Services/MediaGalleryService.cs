using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>
/// Shared plumbing for gallery posts (TikTok photo posts, Facebook stories): one browser-like HTTP
/// client, media downloads with mirror fallback, and saving picked items as loose files or one zip.
/// </summary>
public static class MediaGalleryService
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    // UseCookies=false: each scraper sends its site's cookies as a hand-built Cookie header, so the
    // imported jar never leaks to CDN hosts or other sites.
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>GETs a page as a browser navigation would, with an optional Cookie header. Returns the body and the final (post-redirect) URL.</summary>
    public static async Task<(string Html, Uri FinalUri)> GetPageAsync(string url, string cookieHeader, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        if (!string.IsNullOrEmpty(cookieHeader)) request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        return (html, response.RequestMessage?.RequestUri ?? new Uri(url));
    }

    /// <summary>Downloads the first URL in <paramref name="urls"/> that answers (mirrors / lower qualities as fallbacks).</summary>
    public static async Task<byte[]> DownloadAsync(IEnumerable<string> urls, string? referer, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (referer is not null) request.Headers.Referrer = new Uri(referer);
                using var response = await Http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        throw new InvalidOperationException(last?.Message ?? "No download URL.");
    }

    /// <summary>
    /// Runs the item's lazy full-size lookup once (if it has one) and puts the result in front of its
    /// preview URLs. A failed lookup just leaves the preview as the download — never fails the save.
    /// </summary>
    public static async Task EnsureFullUrlsAsync(MediaGalleryItem item, CancellationToken ct = default)
    {
        if (item.ResolveFullUrlsAsync is not { } resolve) return;
        item.ResolveFullUrlsAsync = null;
        try
        {
            var full = await resolve(ct);
            item.Urls.InsertRange(0, full.Where(u => !item.Urls.Contains(u)));
        }
        catch (OperationCanceledException) { throw; }
        catch { /* keep the preview URL */ }
    }

    /// <summary>Full-size bytes of a gallery item (resolving lazy photos first). Photo bytes are cached on the item; video bytes are not.</summary>
    public static async Task<byte[]> GetItemBytesAsync(MediaGalleryItem item, string? referer, CancellationToken ct = default)
    {
        if (item.Bytes is { } cached) return cached;
        await EnsureFullUrlsAsync(item, ct);
        var data = await DownloadAsync(item.Urls, referer, ct);
        if (!item.IsVideo) item.Bytes = data;
        return data;
    }

    /// <summary>Writes the given items as loose files into <paramref name="folder"/>. Returns the written paths.</summary>
    public static async Task<List<string>> SaveToFolderAsync(
        MediaGalleryPost post, IReadOnlyList<(MediaGalleryItem Item, byte[] Data)> items, string folder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var written = new List<string>();
        foreach (var (item, data) in items)
        {
            ct.ThrowIfCancellationRequested();
            var path = UniquePath(Path.Combine(folder, FileNameFor(post, item, data)));
            await File.WriteAllBytesAsync(path, data, ct);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Packs the given items into one .zip at <paramref name="zipPath"/> (never overwrites — picks a free name).</summary>
    public static async Task<string> SaveAsZipAsync(
        MediaGalleryPost post, IReadOnlyList<(MediaGalleryItem Item, byte[] Data)> items, string zipPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        zipPath = UniquePath(zipPath);

        await using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (item, data) in items)
        {
            ct.ThrowIfCancellationRequested();
            // Photos/videos are already compressed — Store avoids burning CPU for ~0% gain.
            var entry = archive.CreateEntry(FileNameFor(post, item, data), CompressionLevel.NoCompression);
            await using var entryStream = entry.Open();
            await entryStream.WriteAsync(data, ct);
        }
        return zipPath;
    }

    /// <summary>"author_postId" — used for the per-post subfolder and the zip name.</summary>
    public static string BaseNameFor(MediaGalleryPost post) => SanitizeFileName($"{post.Author}_{post.Id}");

    private static string FileNameFor(MediaGalleryPost post, MediaGalleryItem item, byte[] data) =>
        $"{BaseNameFor(post)}_{item.Index:D2}.{item.ExtensionFor(data)}";

    /// <summary>
    /// Builds a Cookie header from the entries of a Netscape cookies.txt whose domain satisfies
    /// <paramref name="domainFilter"/> (expired ones skipped).
    /// </summary>
    public static string BuildCookieHeader(string? cookiesText, Func<string, bool> domainFilter)
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
            if (!domainFilter(parts[0].TrimStart('.'))) continue;
            if (long.TryParse(parts[4], out var expiry) && expiry > 0 && expiry < now) continue;

            var name = parts[5];
            var value = parts[6];
            // Header values must stay single-line ASCII; drop anything that would make the header invalid.
            if (name.Length == 0 || (name + value).Any(c => c < 0x20 || c > 0x7E || c == ';')) continue;
            pairs[name] = value;
        }
        return string.Join("; ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>True when <paramref name="host"/> is <paramref name="domain"/> or one of its subdomains.</summary>
    public static bool IsHostOf(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static string SanitizeFileName(string name)
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

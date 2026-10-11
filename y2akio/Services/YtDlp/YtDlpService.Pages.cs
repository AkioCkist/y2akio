using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace y2akio.Services;

/// <summary>Raw page fetching through yt-dlp's browser impersonation.</summary>
public partial class YtDlpService
{
    /// <summary>
    /// Returns the raw pages yt-dlp's extractor downloads for <paramref name="url"/>, fetched with
    /// <c>--impersonate chrome</c> (curl_cffi, Chrome's TLS fingerprint). Some bot walls fingerprint
    /// .NET's TLS handshake — TikTok's WAF answers every HttpClient request with a JS challenge page
    /// no matter the headers — and this is the one client the app ships that gets the real page.
    /// Pages are written with <c>--write-pages</c> into a throwaway folder and read back; nothing is
    /// downloaded. The exit code is ignored: the extractor may fail after the page was fetched.
    /// </summary>
    /// <param name="cookiesText">Netscape cookies.txt content, or null/empty for anonymous.</param>
    public async Task<IReadOnlyList<string>> FetchPagesAsync(string url, string? cookiesText, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "y2akio-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var args = new StringBuilder("--simulate --no-warnings --no-playlist --impersonate chrome --write-pages ");
            if (!string.IsNullOrWhiteSpace(cookiesText))
            {
                var cookiesPath = Path.Combine(dir, "cookies.txt");
                await File.WriteAllTextAsync(cookiesPath, cookiesText, ct);
                args.Append($"--cookies \"{cookiesPath}\" ");
            }
            args.Append($"\"{url}\"");

            await RunProcessAsync(args.ToString(), ct, workingDirectory: dir);
            return Directory.GetFiles(dir, "*.dump").Select(File.ReadAllText).ToList();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort; holds decrypted cookies */ }
        }
    }
}

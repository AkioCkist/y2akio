using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;
using y2akio.Services.Downloads;

namespace y2akio.Services;

/// <summary>
/// Hands plain-HTTP formats to the multi-connection <see cref="SegmentedDownloader"/> and leaves
/// merging / post-processing to yt-dlp, which skips any format file that already exists.
/// </summary>
public partial class YtDlpService
{
    /// <summary>One file yt-dlp would download for the request, as resolved by <c>yt-dlp -j</c>.</summary>
    private sealed record ResolvedFormat(
        string FormatId,
        string Url,
        string Protocol,
        IReadOnlyDictionary<string, string> Headers,
        long SizeHint,
        bool HasFragments,
        bool NeedsCookies,
        string TargetPath);

    private sealed record ResolvedDownload(string FinalPath, bool Merged, IReadOnlyList<ResolvedFormat> Formats);

    private async Task PrefetchWithEngineAsync(
        DownloadRequest req,
        string url,
        string infoPath,
        string outputTemplate,
        string transcodeArgs,
        DownloadEngineOptions engine,
        IProgress<DownloadProgress> progress,
        List<string> enginePartials,
        Action<string>? onLogLine,
        CancellationToken ct)
    {
        var resolved = await ResolveDownloadAsync(req, url, infoPath, outputTemplate, transcodeArgs, ct);

        if (resolved.Merged && File.Exists(resolved.FinalPath))
            return; // finished earlier; yt-dlp reports "already downloaded" and re-runs post-processing

        var eligible = resolved.Formats.Where(f => IsEngineEligible(f, out _)).ToList();
        foreach (var f in resolved.Formats.Except(eligible))
        {
            IsEngineEligible(f, out var why);
            onLogLine?.Invoke($"[engine] format {f.FormatId}: {why} — yt-dlp downloads it.");
        }
        if (eligible.Count == 0) return;

        // Overall progress across the files (video + audio), weighted by size when yt-dlp knows it.
        var sizes = eligible.ToDictionary(f => f.FormatId, f => Math.Max(f.SizeHint, 0));
        long doneBytes = 0;

        foreach (var format in eligible)
        {
            ct.ThrowIfCancellationRequested();

            if (File.Exists(format.TargetPath) && !DownloadState.Exists(format.TargetPath)
                && (format.SizeHint <= 0 || new FileInfo(format.TargetPath).Length == format.SizeHint))
            {
                onLogLine?.Invoke($"[engine] format {format.FormatId}: already on disk.");
                doneBytes += sizes[format.FormatId];
                continue;
            }

            var connections = IsGoogleVideo(format.Url)
                ? Math.Min(engine.MaxConnections, MaxGoogleVideoConnections)
                : engine.MaxConnections;
            var resuming = DownloadState.Exists(format.TargetPath);
            onLogLine?.Invoke($"[engine] format {format.FormatId}: {(resuming ? "resuming" : "downloading")} with up to {connections} connections → {Path.GetFileName(format.TargetPath)}");

            enginePartials.Add(format.TargetPath);
            engine.OnPartialFile?.Invoke(format.TargetPath);

            var baseDone = doneBytes;
            var segmentProgress = new InlineProgress<SegmentedProgress>(p =>
            {
                if (p.Total > 0) sizes[format.FormatId] = p.Total;
                var total = sizes.Values.Sum();
                var downloaded = baseDone + p.Downloaded;
                var percent = total > 0 ? Math.Min(99.9, downloaded * 100.0 / total) : 0;
                progress.Report(new DownloadProgress
                {
                    Percent = percent,
                    StatusText = FormatEngineStatus(downloaded, total, p),
                    BytesPerSecond = p.BytesPerSecond,
                    DownloadedBytes = downloaded,
                    TotalBytes = total,
                    Eta = p.Eta,
                    Connections = p.ActiveConnections,
                    Segments = p.Segments,
                    SegmentsTotal = p.Total,
                });
            });

            var downloader = new SegmentedDownloader(new SegmentedDownloadOptions
            {
                MaxConnections = connections,
                RateLimiters = engine.RateLimiters,
            });

            var formatId = format.FormatId;
            await downloader.DownloadAsync(
                new DownloadSource(format.Url, format.Headers),
                format.TargetPath,
                segmentProgress,
                refreshSource: async token =>
                {
                    onLogLine?.Invoke($"[engine] format {formatId}: link expired, fetching a fresh one...");
                    return await RefreshFormatSourceAsync(req, url, infoPath, outputTemplate, transcodeArgs, formatId, token);
                },
                ct);

            doneBytes += sizes[format.FormatId] > 0 ? sizes[format.FormatId] : new FileInfo(format.TargetPath).Length;
            onLogLine?.Invoke($"[engine] format {format.FormatId}: done.");
        }

        // Bytes are in; yt-dlp now starts up to merge / post-process. Say so instead of a frozen "100%".
        progress.Report(new DownloadProgress { Percent = 99.9, StatusText = LocalizationService.Get("Downloads.Phase.Processing") });
    }

    /// <summary>googlevideo answers 403 when one client opens too many parallel connections.</summary>
    private const int MaxGoogleVideoConnections = 6;

    private static bool IsGoogleVideo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsEngineEligible(ResolvedFormat f, out string reason)
    {
        if (f.Protocol is not ("https" or "http")) { reason = $"{f.Protocol} stream (fragments)"; return false; }
        if (f.HasFragments) { reason = "fragmented"; return false; }
        if (f.NeedsCookies) { reason = "needs site cookies"; return false; }
        if (string.IsNullOrWhiteSpace(f.Url)) { reason = "no direct URL"; return false; }
        reason = "";
        return true;
    }

    /// <summary>
    /// Asks yt-dlp which formats it would download for this request and under which file names
    /// (<c>-j</c> on the cached info JSON: no network, ~0.6s). Merged downloads store each format as
    /// <c>&lt;name&gt;.f&lt;id&gt;.&lt;ext&gt;</c> before merging, exactly like yt-dlp itself.
    /// </summary>
    private async Task<ResolvedDownload> ResolveDownloadAsync(
        DownloadRequest req, string url, string infoPath, string outputTemplate, string transcodeArgs, CancellationToken ct)
    {
        var args = "-j --no-warnings " + BuildDownloadArgs(req, $"--load-info-json \"{infoPath}\" ", outputTemplate, CookiesArg(req.CookiesPath, url), transcodeArgs);
        var (stdout, stderr, exitCode) = await RunProcessAsync(args, ct);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException("Could not resolve formats: " + FirstLine(stderr));

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var finalPath = GetString(root, "filename") ?? GetString(root, "_filename")
            ?? throw new InvalidOperationException("yt-dlp did not report a file name.");

        var formats = new List<ResolvedFormat>();
        if (root.TryGetProperty("requested_formats", out var requested) && requested.ValueKind == JsonValueKind.Array)
        {
            // Same rule as yt-dlp: strip the merged extension, then ".f<id>.<format ext>".
            var stem = Path.Combine(Path.GetDirectoryName(finalPath) ?? "", Path.GetFileNameWithoutExtension(finalPath));
            foreach (var f in requested.EnumerateArray())
            {
                var id = GetString(f, "format_id") ?? "";
                var ext = GetString(f, "ext") ?? "bin";
                formats.Add(ToResolved(f, $"{stem}.f{id}.{ext}"));
            }
            return new ResolvedDownload(finalPath, true, formats);
        }

        formats.Add(ToResolved(root, finalPath));
        return new ResolvedDownload(finalPath, false, formats);
    }

    private static ResolvedFormat ToResolved(JsonElement f, string target)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (f.TryGetProperty("http_headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String) headers[p.Name] = p.Value.GetString() ?? "";
            }
        }

        // Exact size only: filesize_approx is not good enough to tell a complete file from a truncated one.
        long size = 0;
        if (f.TryGetProperty("filesize", out var fs) && fs.ValueKind == JsonValueKind.Number) size = (long)fs.GetDouble();

        return new ResolvedFormat(
            GetString(f, "format_id") ?? "",
            GetString(f, "url") ?? "",
            GetString(f, "protocol") ?? "",
            headers,
            size,
            f.TryGetProperty("fragments", out var frags) && frags.ValueKind == JsonValueKind.Array,
            !string.IsNullOrWhiteSpace(GetString(f, "cookies")),
            target);
    }

    /// <summary>Re-extracts the link (signed URLs expired), refreshes the cached info JSON, and returns the same format's new URL.</summary>
    private async Task<DownloadSource?> RefreshFormatSourceAsync(
        DownloadRequest req, string url, string infoPath, string outputTemplate, string transcodeArgs, string formatId, CancellationToken ct)
    {
        try
        {
            var json = await RunDumpJsonAsync(req.Url, flat: false, req.CookiesPath, ct);
            await File.WriteAllTextAsync(infoPath, json, new UTF8Encoding(false), ct);
            var resolved = await ResolveDownloadAsync(req, url, infoPath, outputTemplate, transcodeArgs, ct);
            var match = resolved.Formats.FirstOrDefault(f => f.FormatId == formatId);
            return match is null ? null : new DownloadSource(match.Url, match.Headers);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// After a successful run: drop resume files, and per-format files yt-dlp did not consume (it
    /// deletes the ones it merged; leftovers mean it downloaded the format itself under another name).
    /// </summary>
    private static void CleanupEngineFiles(IEnumerable<string> targets, string? finalPath)
    {
        foreach (var target in targets)
        {
            DownloadState.Delete(target);
            // Only delete leftovers when the final file is known to exist; otherwise the "leftover"
            // might be the result itself.
            if (finalPath is null || !File.Exists(finalPath)) continue;
            try
            {
                if (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(finalPath), StringComparison.OrdinalIgnoreCase))
                    File.Delete(target);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>yt-dlp failures worth simply running again (it resumes .part files and fragments).</summary>
    private static bool IsTransientNetworkError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        string[] markers =
        {
            "timed out", "Connection reset", "Connection aborted", "Connection refused", "Remote end closed",
            "IncompleteRead", "HTTP Error 500", "HTTP Error 502", "HTTP Error 503", "HTTP Error 504",
            "getaddrinfo failed", "Temporary failure in name resolution", "No route to host",
            "Network is unreachable", "giving up after", "Unable to download video data",
        };
        return markers.Any(m => stderr.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatEngineStatus(long downloaded, long total, SegmentedProgress p)
    {
        var sb = new StringBuilder();
        sb.Append(SegmentedDownloader.FormatBytes(downloaded));
        if (total > 0) sb.Append(" / ").Append(SegmentedDownloader.FormatBytes(total));
        if (p.BytesPerSecond > 0) sb.Append(" · ").Append(SegmentedDownloader.FormatBytes(p.BytesPerSecond)).Append("/s");
        if (p.ActiveConnections > 0)
            sb.Append(" · ").Append(p.ActiveConnections).Append(p.ActiveConnections == 1 ? " connection" : " connections");
        if (p.Eta is { } eta) sb.Append(" · ETA ").Append(eta.TotalHours >= 1 ? eta.ToString(@"h\:mm\:ss") : eta.ToString(@"m\:ss"));
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex YtDlpProgressRegex = new(
        @"\[download\]\s+[\d.]+% of\s+~?\s*(?<size>[\d.]+\s*\w+)(?:\s+at\s+(?<speed>[\d.]+\s*\w+/s))?(?:\s+ETA\s+(?<eta>[\d:]+))?(?:\s+\(frag (?<frag>\d+)/(?<frags>\d+)\))?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>"[download]  45.3% of ~12.34MiB at 2.50MiB/s ETA 00:05 (frag 3/120)" → "12.34 MiB · 2.50 MiB/s · ETA 00:05 · 3/120".</summary>
    private static string FriendlyProgressText(string line, double percent)
    {
        var m = YtDlpProgressRegex.Match(line);
        if (!m.Success) return line.Trim();

        // "100% of X" without a speed is yt-dlp confirming a file that is already complete
        // (fetched by the engine): the next step is post-processing.
        if (percent >= 100 && !m.Groups["speed"].Success)
            return LocalizationService.Get("Downloads.Phase.Processing");

        static string Unit(string s) => System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"(?<=\d)(?=[A-Za-z])", " ");
        var parts = new List<string> { Unit(m.Groups["size"].Value) };
        if (m.Groups["speed"].Success) parts.Add(Unit(m.Groups["speed"].Value));
        if (m.Groups["eta"].Success) parts.Add("ETA " + m.Groups["eta"].Value);
        if (m.Groups["frag"].Success) parts.Add($"{m.Groups["frag"].Value}/{m.Groups["frags"].Value}");
        return $"{percent:0.#}% · " + string.Join(" · ", parts);
    }

    /// <summary>Readable text for a yt-dlp post-processor line, or null for any other line.</summary>
    private static string? PhaseText(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"^\[(\w+)\]");
        if (!m.Success) return null;
        var key = m.Groups[1].Value switch
        {
            "Merger" => "Merging",
            "Metadata" => "Metadata",
            "ThumbnailsConvertor" or "EmbedThumbnail" => "Thumbnail",
            "ExtractAudio" => "Audio",
            "VideoConvertor" or "VideoRemuxer" => "Video",
            "EmbedSubtitle" or "SubtitlesConvertor" => "Subtitles",
            var t when t.StartsWith("Fixup", StringComparison.Ordinal) => "Fixup",
            _ => null,
        };
        return key is null ? null : LocalizationService.Get("Downloads.Phase." + key);
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown error";
        var line = text.Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }

    /// <summary>IProgress that runs inline on the reporting thread (the caller's Progress&lt;T&gt; marshals to the UI).</summary>
    private sealed class InlineProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}

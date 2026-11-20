using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>Download command construction, progress reporting, cleanup, and output resolution.</summary>
public partial class YtDlpService
{
    /// <param name="engine">Multi-connection engine settings; null = plain yt-dlp download (old behavior).</param>
    public async Task<string> DownloadAsync(
        DownloadRequest req,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default,
        Action<string>? onLogLine = null,
        DownloadEngineOptions? engine = null)
    {
        Directory.CreateDirectory(req.SaveFolder);

        var url = NormalizeUrl(req.Url);
        var outputTemplate = BuildOutputTemplate(req);

        string? resolvedPath = null;
        var destinationRegex = new Regex(@"Destination:\s*(.+)$", RegexOptions.Compiled);
        var mergeRegex = new Regex(@"Merging formats into\s*""(.+)""", RegexOptions.Compiled);
        var progressRegex = new Regex(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);
        var alreadyRegex = new Regex(@"\[download\]\s+(.+?) has already been downloaded", RegexOptions.Compiled);

        // Files present before this download, so cleanup only touches what this run created.
        var priorFiles = SnapshotFileNames(req.SaveFolder);
        var startedAt = DateTime.Now;

        void OnLine(string line)
        {
            onLogLine?.Invoke(line);

            var pMatch = progressRegex.Match(line);
            if (pMatch.Success && double.TryParse(pMatch.Groups[1].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                progress.Report(new DownloadProgress { Percent = pct, StatusText = FriendlyProgressText(line, pct) });
                return;
            }

            // Post-processing steps ([Merger], [EmbedThumbnail], ...): a readable phase instead of the raw line.
            if (PhaseText(line) is { } phase)
                progress.Report(new DownloadProgress { Percent = 100, StatusText = phase });

            var dMatch = destinationRegex.Match(line);
            if (dMatch.Success) resolvedPath = dMatch.Groups[1].Value.Trim();

            var mMatch = mergeRegex.Match(line);
            if (mMatch.Success) resolvedPath = mMatch.Groups[1].Value.Trim();

            // yt-dlp skipped the download because the target file exists — that file is the result.
            var aMatch = alreadyRegex.Match(line);
            if (aMatch.Success) resolvedPath = aMatch.Groups[1].Value.Trim();
        }

        var transcodeArgs = req.Kind == MediaKind.Video && req.VideoCodec != "auto"
            ? await ResolveTranscodeArgsAsync(req.VideoCodec)
            : "";

        // Reuse the analyze step's info JSON when it is fresh: yt-dlp then skips the page/player
        // extraction and JS challenge (measured ~0.6s vs 2-4s before the first byte). Signed media
        // URLs expire after a few hours, so stale JSON is not reused.
        var infoJson = !string.IsNullOrWhiteSpace(req.InfoJson) && DateTime.UtcNow - req.InfoJsonAnalyzedAtUtc < InfoJsonMaxAge
            ? req.InfoJson
            : null;

        // The multi-connection engine needs the media URLs, i.e. an info JSON. Playlist entries and
        // stale analyses get one fresh extraction here; yt-dlp then reuses it instead of extracting again.
        var useEngine = engine?.Enabled == true;
        if (useEngine && infoJson is null)
        {
            try
            {
                onLogLine?.Invoke("Extracting media links...");
                infoJson = await RunDumpJsonAsync(req.Url, flat: false, req.CookiesPath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                onLogLine?.Invoke($"Could not extract media links ({FirstLine(ex.Message)}); yt-dlp will download instead.");
                useEngine = false;
            }
        }

        string? infoPath = null;
        if (infoJson is not null)
        {
            try
            {
                infoPath = Path.Combine(Path.GetTempPath(), $"y2akio-{Guid.NewGuid():N}.info.json");
                await File.WriteAllTextAsync(infoPath, infoJson, new UTF8Encoding(false), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                infoPath = null;
                useEngine = false;
            }
        }

        var enginePartials = new List<string>();
        string lastError = "";
        var lastExitCode = 1;
        try
        {
            // Step 1: the multi-connection engine fetches every plain-HTTP format to the exact file
            // name yt-dlp would use. yt-dlp then finds the files "already downloaded" and only merges
            // and post-processes. Anything the engine cannot do is left to yt-dlp below.
            if (useEngine && infoPath is not null)
            {
                try
                {
                    await PrefetchWithEngineAsync(req, url, infoPath, outputTemplate, transcodeArgs, engine!, progress, enginePartials, onLogLine, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    onLogLine?.Invoke($"Multi-connection download stopped ({FirstLine(ex.Message)}); yt-dlp takes over.");
                }
            }

            // Step 2: yt-dlp. Attempt list: cached info JSON first (when available), then the same
            // fallback chain as analyze (YouTube Android client / Facebook anonymous-then-cookies).
            var attempts = new List<(string Source, string Extra, bool FromInfo)>();
            if (infoPath is not null)
                attempts.Add(($"--load-info-json \"{infoPath}\" ", CookiesArg(req.CookiesPath, url), true));
            foreach (var attemptArgs in AttemptArgs(url, req.CookiesPath))
                attempts.Add(($"\"{url}\"", attemptArgs, false));

            // A failed attempt's log lines are already visible in the log panel.
            foreach (var (source, extra, fromInfo) in attempts)
            {
                // Network hiccups: yt-dlp resumes its .part file / fragments (--continue), so the same
                // attempt simply runs again a few times before moving on.
                for (var networkRetry = 0; ; networkRetry++)
                {
                    var args = BuildDownloadArgs(req, source, outputTemplate, extra, transcodeArgs, engine);
                    var (_, stderr, exitCode) = await RunProcessAsync(args, ct, OnLine);
                    lastError = stderr;
                    lastExitCode = exitCode;
                    if (exitCode == 0 || networkRetry >= 3 || !IsTransientNetworkError(stderr)) break;

                    var wait = TimeSpan.FromSeconds(Math.Min(2 << networkRetry, 30));
                    onLogLine?.Invoke($"Network error; resuming in {wait.TotalSeconds:0}s (retry {networkRetry + 1}/3)...");
                    await Task.Delay(wait, ct);
                }

                if (lastExitCode == 0) break;
                if (fromInfo)
                {
                    // Expired links, a 403 from the CDN, ... — a fresh extraction usually fixes it.
                    onLogLine?.Invoke("Cached analysis could not be used; extracting the link again...");
                    continue;
                }
                if (!IsRetryable(url, lastError)) break;
            }
        }
        finally
        {
            if (infoPath is not null)
            {
                try { File.Delete(infoPath); } catch { /* best-effort */ }
            }
        }

        if (lastExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "Download failed." : lastError.Trim());

        // The printed path can still be off (odd characters, a renamed post-processor output): make
        // sure we report — and protect from cleanup — the file that really exists.
        resolvedPath = Helpers.MediaPathRepair.ResolveOutput(resolvedPath, req.SaveFolder, priorFiles, startedAt);

        CleanupLeftoverFiles(req, priorFiles);
        CleanupEngineFiles(enginePartials, resolvedPath);

        progress.Report(new DownloadProgress { Percent = 100, StatusText = "Done" });
        return resolvedPath ?? Path.Combine(req.SaveFolder, "(saved)");
    }

    /// <summary>Cached info JSON older than this is not reused (YouTube's signed URLs live ~6h).</summary>
    private static readonly TimeSpan InfoJsonMaxAge = TimeSpan.FromHours(3);

    /// <summary>Builds the full yt-dlp command line for a download request.</summary>
    /// <param name="source">The quoted URL, or a <c>--load-info-json "path"</c> argument.</param>
    /// <param name="attemptArgs">This attempt's cookies / extractor-args from <see cref="AttemptArgs"/>.</param>
    /// <param name="transcodeArgs">ffmpeg encoder flags for a forced codec (from <see cref="ResolveTranscodeArgsAsync"/>).</param>
    private string BuildDownloadArgs(DownloadRequest req, string source, string outputTemplate, string attemptArgs, string transcodeArgs,
        DownloadEngineOptions? engine = null)
    {
        var args = new StringBuilder();
        args.Append("--newline "); // one progress line per update, easier to parse
        // HLS / DASH fragments (Facebook, X, TikTok, livestreams...) download several at a time instead
        // of one by one — as many as the engine's connection setting. Plain HTTP formats are unaffected.
        var fragments = Math.Clamp(engine?.MaxConnections ?? 4, 1, 16);
        args.Append($"--concurrent-fragments {fragments} ");
        // Ride out flaky networks: per-request and per-fragment retries with exponential backoff, and
        // a socket timeout so a stalled connection is retried instead of hanging forever.
        args.Append("--retries 10 --fragment-retries 20 --retry-sleep http:exp=1:30 --retry-sleep fragment:exp=1:30 --socket-timeout 20 ");
        if (engine?.CurrentRateLimitBytes() is long limit and > 0)
            args.Append($"--limit-rate {Math.Max(1, limit / 1024)}K ");
        args.Append(FfmpegLocationArg());
        args.Append(JsRuntimeArg());
        args.Append(ImpersonateArg(req.Url));
        args.Append(attemptArgs);
        args.Append($"-o \"{outputTemplate}\" ");

        if (req.Kind == MediaKind.Audio)
        {
            args.Append("-x --audio-format " + req.AudioFormat + " ");
            args.Append(req.AudioBitrateKbps is int kbps
                ? $"--audio-quality {kbps}k "
                : "--audio-quality 0 "); // 0 = best (lossless formats ignore this anyway)
        }
        else
        {
            var heightFilter = req.TargetHeight is int h ? $"[height<={h}]" : "";
            args.Append($"-f \"bestvideo{heightFilter}+bestaudio/best{heightFilter}\" ");

            if (req.VideoCodec != "auto")
            {
                // Force transcode: merge to mkv first (--recode-video mp4 SKIPS if the merged container
                // is already mp4), then convert to the chosen codec. Saved file is always the chosen
                // codec in MP4, whatever the source was (AV1/VP9/H.264). Requires ffmpeg.
                args.Append("--merge-output-format mkv ");
                args.Append("--recode-video mp4 ");
                args.Append($"--postprocessor-args \"recode:{transcodeArgs} -c:a aac -c:s mov_text\" ");
            }
            else
            {
                args.Append($"--merge-output-format {req.ContainerFormat} ");
            }
        }

        if (req.EmbedMetadata)
            args.Append("--add-metadata ");

        switch (req.ThumbnailMode)
        {
            case "embed":
                args.Append("--embed-thumbnail ");
                break;
            case "save":
                args.Append("--write-thumbnail ");
                break;
        }

        if (req.Kind == MediaKind.Video && req.SubtitlesEnabled && (req.SubtitleManual || req.SubtitleAuto))
        {
            if (req.SubtitleManual) args.Append("--write-subs ");
            if (req.SubtitleAuto) args.Append("--write-auto-subs ");
            args.Append($"--sub-langs \"{req.SubtitleLangCode}\" ");
            args.Append($"--convert-subs {req.SubtitleFormat} "); // requires ffmpeg

            if (req.EmbedSubtitles)
                args.Append("--embed-subs "); // embed into the video, don't leave loose files
        }

        args.Append(source);
        return args.ToString();
    }

    /// <summary>
    /// True when a YouTube failure looks like the web-client bot-check (worth retrying via the
    /// Android client) rather than a definitive problem (private/region-locked video, bad ID, ...).
    /// </summary>
    private static bool IsYoutubeClientRetryable(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("This video is not available", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase)
            || error.Contains("needs to be reloaded", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ffmpeg encoder + quality flags for a forced codec transcode. AV1 uses SVT-AV1
    /// (<c>libsvtav1</c>) rather than the reference libaom encoder — libaom is several times slower
    /// for the same quality, far too slow for an interactive download. VP9 needs <c>-b:v 0</c> so
    /// the CRF actually governs bitrate. MPEG-4 uses the part-2 quality scale (<c>-q:v</c>, lower = better).
    /// </summary>
    private static string CodecTranscodeArgs(string codec) => codec switch
    {
        "h264" => "-c:v libx264 -preset medium -crf 18",
        "hevc" => "-c:v libx265 -preset medium -crf 23",
        "av1" => "-c:v libsvtav1 -preset 6 -crf 30",
        "vp9" => "-c:v libvpx-vp9 -crf 31 -b:v 0 -row-mt 1",
        "mpeg4" => "-c:v mpeg4 -q:v 3",
        _ => "",
    };

    /// <summary>
    /// Deletes loose sidecar files this run created, when the user asked for them to be embedded:
    /// subtitle tracks (*.srt/*.vtt/*.ass), thumbnail images (*.webp/*.jpg/...), and stale *.part
    /// fragments. Only files that did not exist before the download are removed, so a previous
    /// "save separate file" download in the same folder is left untouched.
    /// </summary>
    private static void CleanupLeftoverFiles(DownloadRequest req, HashSet<string> priorFiles)
    {
        try
        {
            if (!Directory.Exists(req.SaveFolder)) return;
            var created = Directory.EnumerateFiles(req.SaveFolder)
                .Where(f => !priorFiles.Contains(f))
                .ToList();

            var subtitleExts = req.Kind == MediaKind.Video && req.EmbedSubtitles
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa", ".sub" }
                : null;
            var thumbnailExts = req.ThumbnailMode == "embed"
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".webp", ".jpg", ".jpeg", ".png" }
                : null;

            foreach (var file in created)
            {
                var ext = Path.GetExtension(file);
                if ((subtitleExts is not null && subtitleExts.Contains(ext)) ||
                    (thumbnailExts is not null && thumbnailExts.Contains(ext)) ||
                    string.Equals(ext, ".part", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { /* ignore */ }
                }
            }
        }
        catch
        {
            // Cleanup is cosmetic; never fail the download over it.
        }
    }

    private static HashSet<string> SnapshotFileNames(string dir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir))
                    set.Add(f);
        }
        catch { /* ignore */ }
        return set;
    }

    private static string BuildOutputTemplate(DownloadRequest req)
    {
        // yt-dlp output template pieces: %(title)s, %(uploader)s, %(ext)s
        var parts = new System.Collections.Generic.List<string>();
        // RequestedAt, not "now": a paused and resumed job must map to the same file names.
        if (req.PrefixDateTime) parts.Add(req.RequestedAt.ToString("yyyyMMdd_HHmmss"));
        if (req.PrefixChannel) parts.Add("%(uploader)s");
        // Facebook / Instagram titles are often generic ("Facebook", "Video by …"), so two different
        // videos would map to the same file name — yt-dlp then reports "has already been downloaded",
        // skips the new video and the post-processors rewrite the OLD file. The id keeps names unique.
        var title = IsFacebookUrl(req.Url) || IsInstagramUrl(req.Url) ? "%(title)s [%(id)s]" : "%(title)s";
        if (req.IncludeTitle) parts.Add(title);
        if (parts.Count == 0) parts.Add(title);

        var fileNamePattern = string.Join(" - ", parts);
        return Path.Combine(req.SaveFolder, $"{fileNamePattern}.%(ext)s");
    }

    private string FfmpegLocationArg() =>
        string.IsNullOrWhiteSpace(_ffmpegDirectory) ? "" : $"--ffmpeg-location \"{_ffmpegDirectory}\" ";

    /// <summary>
    /// Points yt-dlp at the bundled deno for YouTube's JS challenges. Without a JS runtime yt-dlp
    /// falls back to degraded clients (missing formats, more bot checks, slower analyze), and it
    /// only auto-detects deno on PATH — so a clean machine would silently run without one.
    /// </summary>
    private string JsRuntimeArg() =>
        string.IsNullOrWhiteSpace(_denoPath) ? "" : $"--js-runtimes \"deno:{_denoPath}\" ";

}

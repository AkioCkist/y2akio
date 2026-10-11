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
    public async Task<string> DownloadAsync(
        DownloadRequest req,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default,
        Action<string>? onLogLine = null)
    {
        Directory.CreateDirectory(req.SaveFolder);

        var url = NormalizeUrl(req.Url);
        var outputTemplate = BuildOutputTemplate(req);

        string? resolvedPath = null;
        var destinationRegex = new Regex(@"Destination:\s*(.+)$", RegexOptions.Compiled);
        var mergeRegex = new Regex(@"Merging formats into\s*""(.+)""", RegexOptions.Compiled);
        var progressRegex = new Regex(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

        // Files present before this download, so cleanup only touches what this run created.
        var priorFiles = SnapshotFileNames(req.SaveFolder);

        void OnLine(string line)
        {
            onLogLine?.Invoke(line);

            var pMatch = progressRegex.Match(line);
            if (pMatch.Success && double.TryParse(pMatch.Groups[1].Value, out var pct))
            {
                progress.Report(new DownloadProgress { Percent = pct, StatusText = line.Trim() });
                return;
            }

            var dMatch = destinationRegex.Match(line);
            if (dMatch.Success) resolvedPath = dMatch.Groups[1].Value.Trim();

            var mMatch = mergeRegex.Match(line);
            if (mMatch.Success) resolvedPath = mMatch.Groups[1].Value.Trim();
        }

        // Same fallback chain as analyze (YouTube Android client / Facebook anonymous-then-cookies).
        // A failed attempt's log lines are already visible in the log panel.
        string lastError = "";
        var lastExitCode = 1;
        foreach (var attemptArgs in AttemptArgs(url, req.CookiesPath))
        {
            var args = BuildDownloadArgs(req, url, outputTemplate, attemptArgs);
            var (_, stderr, exitCode) = await RunProcessAsync(args.ToString(), ct, OnLine);
            lastError = stderr;
            lastExitCode = exitCode;
            if (exitCode == 0) break;
            if (!IsRetryable(url, lastError)) break;
        }

        if (lastExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastError) ? "Download failed." : lastError.Trim());

        CleanupLeftoverFiles(req, priorFiles);

        progress.Report(new DownloadProgress { Percent = 100, StatusText = "Done" });
        return resolvedPath ?? Path.Combine(req.SaveFolder, "(saved)");
    }

    /// <summary>Builds the full yt-dlp command line for a download request.</summary>
    /// <param name="attemptArgs">This attempt's cookies / extractor-args from <see cref="AttemptArgs"/>.</param>
    private string BuildDownloadArgs(DownloadRequest req, string url, string outputTemplate, string attemptArgs)
    {
        var args = new StringBuilder();
        args.Append("--newline "); // one progress line per update, easier to parse
        args.Append(FfmpegLocationArg());
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
                args.Append($"--postprocessor-args \"recode:{CodecTranscodeArgs(req.VideoCodec)} -c:a aac -c:s mov_text\" ");
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

        args.Append($"\"{url}\"");
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
        if (req.PrefixDateTime) parts.Add(DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        if (req.PrefixChannel) parts.Add("%(uploader)s");
        if (req.IncludeTitle) parts.Add("%(title)s");
        if (parts.Count == 0) parts.Add("%(title)s");

        var fileNamePattern = string.Join(" - ", parts);
        return Path.Combine(req.SaveFolder, $"{fileNamePattern}.%(ext)s");
    }

    private string FfmpegLocationArg() =>
        string.IsNullOrWhiteSpace(_ffmpegDirectory) ? "" : $"--ffmpeg-location \"{_ffmpegDirectory}\" ";

}

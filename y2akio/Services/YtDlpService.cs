using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using y2akio.Models;

namespace y2akio.Services;

/// <summary>Options collected from the "Download Options" panel.</summary>
public class DownloadRequest
{
    public required string Url { get; init; }
    public required MediaKind Kind { get; init; }
    public required string SaveFolder { get; init; }

    // Video-specific
    public int? TargetHeight { get; init; }        // null = "Best Quality"
    public string ContainerFormat { get; init; } = "mp4"; // mp4 / mkv / webm
    public string VideoCodec { get; init; } = "auto";    // "auto" / "h264" / "hevc" / "av1" / "vp9" / "mpeg4" — non-auto forces a transcode

    // Audio-specific
    public int? AudioBitrateKbps { get; init; }     // 320/256/192/128, null = best
    public string AudioFormat { get; init; } = "mp3"; // mp3/m4a/aac/flac/opus/vorbis/wav/alac/wma

    // Filename prefix toggles
    public bool PrefixDateTime { get; init; }
    public bool PrefixChannel { get; init; }
    public bool IncludeTitle { get; init; } = true;

    // Subtitles (video only — the audio flow never sets these)
    public bool SubtitlesEnabled { get; init; }
    public bool SubtitleManual { get; init; } = true;   // author-uploaded
    public bool SubtitleAuto { get; init; }              // auto-generated
    public string SubtitleLangCode { get; init; } = "all"; // "all" or a specific code like "en"
    public string SubtitleFormat { get; init; } = "srt";   // "srt" or "vtt"
    public bool EmbedSubtitles { get; init; } = true;      // true = embed into file, false = save alongside

    public bool EmbedMetadata { get; init; } = true;

    /// <summary>Path to a Netscape-format cookies.txt file, passed to yt-dlp via --cookies (needed for login-gated sites like X/Twitter). Empty = no cookies.</summary>
    public string CookiesPath { get; init; } = "";

    // "embed" -> --embed-thumbnail, "save" -> write thumbnail alongside file, "none" -> skip
    public string ThumbnailMode { get; init; } = "embed";
}

public class DownloadProgress
{
    public double Percent { get; init; }
    public string StatusText { get; init; } = "";
}

public class YtDlpService
{
    // Adjust these if yt-dlp.exe / ffmpeg.exe aren't on PATH. Ship them next to the app
    // (e.g. in a "bin" subfolder) and point these at that folder for a self-contained install.
    private readonly string _ytDlpPath;
    private readonly string _ffmpegDirectory;

    public YtDlpService(string ytDlpPath = "yt-dlp.exe", string ffmpegDirectory = "")
    {
        _ytDlpPath = ytDlpPath;
        _ffmpegDirectory = ffmpegDirectory;
    }

    /// <summary>
    /// Analyzes a pasted link. Runs a fast `--flat-playlist` probe first: if the link turns out to be a
    /// playlist with more than one entry, returns basic metadata for every track (no per-video formats yet —
    /// those get fetched lazily via <see cref="AnalyzeVideoAsync"/> when a specific track is clicked).
    /// Otherwise falls through to a full single-video analyze.
    /// </summary>
    /// <param name="cookiesPath">Netscape cookies.txt path for login-gated sites (X/Twitter, ...). Null/empty = no cookies.</param>
    public async Task<AnalyzeResult> AnalyzeAsync(string url, string? cookiesPath = null, CancellationToken ct = default)
    {
        var flatJson = await RunDumpJsonAsync(url, flat: true, cookiesPath, ct);

        using (var doc = JsonDocument.Parse(flatJson))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("entries", out var entriesEl)
                && entriesEl.ValueKind == JsonValueKind.Array
                && entriesEl.GetArrayLength() > 1)
            {
                var playlist = PlaylistInfo.FromFlatJson(root, url);
                return new AnalyzeResult { IsPlaylist = true, Playlist = playlist };
            }
        }

        var video = await AnalyzeVideoAsync(url, cookiesPath, ct);
        return new AnalyzeResult { IsPlaylist = false, Video = video };
    }

    /// <summary>Runs a full (non-flat) `yt-dlp --dump-single-json <url>` for one specific video, formats included.</summary>
    /// <param name="cookiesPath">Netscape cookies.txt path for login-gated sites (X/Twitter, ...). Null/empty = no cookies.</param>
    public async Task<VideoInfo> AnalyzeVideoAsync(string url, string? cookiesPath = null, CancellationToken ct = default)
    {
        var json = await RunDumpJsonAsync(url, flat: false, cookiesPath, ct);
        return VideoInfo.FromJson(json, url);
    }

    private async Task<string> RunDumpJsonAsync(string url, bool flat, string? cookiesPath, CancellationToken ct)
    {
        url = NormalizeUrl(url);

        var args = new StringBuilder()
            .Append("--dump-single-json --no-warnings ")
            .Append(flat ? "--flat-playlist --playlist-items 1-500 " : "--no-playlist ")
            .Append(FfmpegLocationArg())
            .Append(CookiesArg(cookiesPath, url))
            .Append(ImpersonateArg(url))
            .Append($"\"{url}\"");

        var (stdout, stderr, exitCode) = await RunProcessAsync(args.ToString(), ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? "yt-dlp failed to analyze this link." : stderr.Trim());
        }

        return stdout;
    }

    /// <summary>
    /// Passes the imported cookie jar to yt-dlp — except for YouTube. The imported file is the
    /// browser's *whole* jar, and sending it to YouTube trips Google's bot check
    /// ("The page needs to be reloaded"); YouTube extracts fine anonymously. Cookies only matter
    /// for login-gated content (X/Twitter, Facebook private/stories, ...).
    /// </summary>
    private static string CookiesArg(string? cookiesPath, string url)
    {
        if (string.IsNullOrWhiteSpace(cookiesPath)) return "";
        if (IsYouTubeUrl(url)) return "";
        return $"--cookies \"{cookiesPath}\" ";
    }

    private static bool IsYouTubeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Facebook/Instagram regularly bot-blocks plain requests, which surfaces as
    /// "[facebook] ...: Cannot parse data". `--impersonate chrome` (curl_cffi TLS fingerprint)
    /// makes the request look like a real browser — the bundled yt-dlp.exe supports it.
    /// Scoped to Facebook hosts so other extractors are unaffected.
    /// </summary>
    private static string ImpersonateArg(string url) =>
        IsFacebookUrl(url) ? "--impersonate chrome " : "";

    private static bool IsFacebookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.EndsWith("facebook.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("fb.watch", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// yt-dlp's facebook extractor does not recognize `/stories/<id>/<token>/` URLs at all
    /// ("Unsupported URL"), even with valid cookies. But the story ID is the video ID, and the
    /// extractor *does* handle `watch/?v=&lt;id&gt;` — so rewrite story links to that form
    /// before passing them on. (Anonymous story links also just redirect to login.php.)
    /// </summary>
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        if (!uri.Host.EndsWith("facebook.com", StringComparison.OrdinalIgnoreCase)) return url;
        if (!uri.AbsolutePath.StartsWith("/stories/", StringComparison.OrdinalIgnoreCase)) return url;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !long.TryParse(segments[1], out _)) return url;

        return $"{uri.Scheme}://{uri.Host}/watch/?v={segments[1]}";
    }

    /// <summary>
    /// Downloads according to the given request, reporting progress parsed from yt-dlp's stdout.
    /// Returns the final saved file path (best-effort — yt-dlp prints it in a "[download] Destination: ..."
    /// or "[Merger] Merging formats into ..." line).
    /// </summary>
    /// <param name="onLogLine">Receives every raw stdout/stderr line (progress, ffmpeg, errors) for a debug log.</param>
    public async Task<string> DownloadAsync(
        DownloadRequest req,
        IProgress<DownloadProgress> progress,
        CancellationToken ct = default,
        Action<string>? onLogLine = null)
    {
        Directory.CreateDirectory(req.SaveFolder);

        var url = NormalizeUrl(req.Url);
        var outputTemplate = BuildOutputTemplate(req);
        var args = new StringBuilder();
        args.Append("--newline "); // one progress line per update, easier to parse
        args.Append(FfmpegLocationArg());
        args.Append(CookiesArg(req.CookiesPath, url));
        args.Append(ImpersonateArg(req.Url));
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

        var (_, stderr, exitCode) = await RunProcessAsync(args.ToString(), ct, OnLine);

        if (exitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Download failed." : stderr.Trim());

        CleanupLeftoverFiles(req, priorFiles);

        progress.Report(new DownloadProgress { Percent = 100, StatusText = "Done" });
        return resolvedPath ?? Path.Combine(req.SaveFolder, "(saved)");
    }

    /// <summary>
    /// ffmpeg encoder + quality flags for a forced codec transcode. AV1 uses SVT-AV1
    /// (<c>libsvtav1</c>) rather than the reference libaom encoder — libaom is several times slower
    /// for the same quality, far too slow for an interactive download. VP9 needs <c>-b:v 0</c> so
    /// the CRF actually governs bitrate. MPEG-4 uses the part-2 quality scale (<c>-q:v</c>, lower = better).
    /// </summary>
    private static string CodecTranscodeArgs(string codec) => codec switch
    {
        "h264"  => "-c:v libx264 -preset medium -crf 18",
        "hevc"  => "-c:v libx265 -preset medium -crf 23",
        "av1"   => "-c:v libsvtav1 -preset 6 -crf 30",
        "vp9"   => "-c:v libvpx-vp9 -crf 31 -b:v 0 -row-mt 1",
        "mpeg4" => "-c:v mpeg4 -q:v 3",
        _       => "",
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

    private async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string arguments, CancellationToken ct, Action<string>? onOutputLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdoutBuilder.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderrBuilder.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data); // yt-dlp writes progress to stderr on some versions too
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using (ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { /* ignore */ } }))
        {
            await process.WaitForExitAsync(ct);
        }

        return (stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode);
    }
}
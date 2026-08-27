using System;
using System.Diagnostics;
using System.IO;
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

    // Audio-specific
    public int? AudioBitrateKbps { get; init; }     // 320/256/192/128, null = best

    // Filename prefix toggles
    public bool PrefixDateTime { get; init; }
    public bool PrefixChannel { get; init; }
    public bool IncludeTitle { get; init; } = true;

    public bool DownloadSubtitles { get; init; }
    public bool EmbedMetadata { get; init; } = true;

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

    /// <summary>Runs `yt-dlp --dump-single-json <url>` and parses the result.</summary>
    public async Task<VideoInfo> AnalyzeAsync(string url, CancellationToken ct = default)
    {
        var args = new StringBuilder()
            .Append("--dump-single-json ")
            .Append("--no-playlist ")
            .Append("--no-warnings ")
            .Append(FfmpegLocationArg())
            .Append($"\"{url}\"");

        var (stdout, stderr, exitCode) = await RunProcessAsync(args.ToString(), ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? "yt-dlp failed to analyze this link." : stderr.Trim());
        }

        return VideoInfo.FromJson(stdout, url);
    }

    /// <summary>
    /// Downloads according to the given request, reporting progress parsed from yt-dlp's stdout.
    /// Returns the final saved file path (best-effort — yt-dlp prints it in a "[download] Destination: ..."
    /// or "[Merger] Merging formats into ..." line).
    /// </summary>
    public async Task<string> DownloadAsync(DownloadRequest req, IProgress<DownloadProgress> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(req.SaveFolder);

        var outputTemplate = BuildOutputTemplate(req);
        var args = new StringBuilder();
        args.Append("--newline "); // one progress line per update, easier to parse
        args.Append(FfmpegLocationArg());
        args.Append($"-o \"{outputTemplate}\" ");

        if (req.Kind == MediaKind.Audio)
        {
            args.Append("-x --audio-format mp3 ");
            args.Append(req.AudioBitrateKbps is int kbps
                ? $"--audio-quality {kbps}k "
                : "--audio-quality 0 "); // 0 = best
        }
        else
        {
            var heightFilter = req.TargetHeight is int h ? $"[height<={h}]" : "";
            args.Append($"-f \"bestvideo{heightFilter}+bestaudio/best{heightFilter}\" ");
            args.Append($"--merge-output-format {req.ContainerFormat} ");
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

        if (req.DownloadSubtitles)
            args.Append("--write-subs --sub-langs all --embed-subs ");

        args.Append($"\"{req.Url}\"");

        string? resolvedPath = null;
        var destinationRegex = new Regex(@"Destination:\s*(.+)$", RegexOptions.Compiled);
        var mergeRegex = new Regex(@"Merging formats into\s*""(.+)""", RegexOptions.Compiled);
        var progressRegex = new Regex(@"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

        void OnLine(string line)
        {
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

        progress.Report(new DownloadProgress { Percent = 100, StatusText = "Done" });
        return resolvedPath ?? Path.Combine(req.SaveFolder, "(saved)");
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

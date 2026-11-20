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

/// <summary>Child-process execution shared by analysis and download operations.</summary>
public partial class YtDlpService
{
    private static int _prewarmed;

    /// <summary>
    /// Runs <c>yt-dlp --version</c> and <c>deno --version</c> once in the background at startup. The
    /// first analyze after boot is otherwise slowed by a cold file cache and antivirus scanning the
    /// onedir <c>_internal</c> DLLs; paying that while the user is still pasting a link hides it.
    /// </summary>
    public Task PrewarmAsync()
    {
        if (Interlocked.Exchange(ref _prewarmed, 1) == 1) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                var tasks = new List<Task> { RunProcessAsync("--version", CancellationToken.None) };
                if (!string.IsNullOrWhiteSpace(_denoPath) && File.Exists(_denoPath))
                    tasks.Add(RunToolAsync(_denoPath, "--version"));
                if (FfmpegExecutable() is { } ffmpeg && File.Exists(ffmpeg))
                    tasks.Add(RunToolAsync(ffmpeg, "-hide_banner -version"));
                await Task.WhenAll(tasks);
            }
            catch
            {
                // purely an optimization
            }
        });
    }

    private static async Task RunToolAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (process is null) return;
        _ = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
    }

    private async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string arguments, CancellationToken ct, Action<string>? onOutputLine = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            // yt-dlp prints in the ANSI code page (1252 on most machines) unless told otherwise, which
            // silently drops emoji and Vietnamese letters from the file paths we parse ("Saved: ...").
            Arguments = "--encoding utf-8 " + arguments,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
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

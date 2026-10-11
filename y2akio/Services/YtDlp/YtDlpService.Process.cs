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
    private async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string arguments, CancellationToken ct, Action<string>? onOutputLine = null, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ytDlpPath,
            Arguments = arguments,
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

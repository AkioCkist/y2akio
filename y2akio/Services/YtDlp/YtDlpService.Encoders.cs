using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace y2akio.Services;

/// <summary>Hardware video encoder selection for forced-codec downloads.</summary>
public partial class YtDlpService
{
    // codec -> ffmpeg encoder flags. Probed once per app run; the probe result never changes mid-session.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> _transcodeArgsCache = new();

    /// <summary>
    /// ffmpeg encoder flags for a forced codec transcode, preferring a GPU encoder (NVENC, Quick Sync,
    /// AMF) that a tiny test encode proves works on this machine, and falling back to the CPU encoder.
    /// A GPU encode is several times faster than libx264/libx265 <c>-preset medium</c>.
    /// </summary>
    private Task<string> ResolveTranscodeArgsAsync(string codec) =>
        _transcodeArgsCache.GetOrAdd(codec, c => new Lazy<Task<string>>(() => ProbeTranscodeArgsAsync(c))).Value;

    private async Task<string> ProbeTranscodeArgsAsync(string codec)
    {
        // Quality targets roughly match the CPU defaults in CodecTranscodeArgs.
        (string Encoder, string Args)[] candidates = codec switch
        {
            "h264" => new[]
            {
                ("h264_nvenc", "-c:v h264_nvenc -preset p5 -rc vbr -cq 19 -b:v 0"),
                ("h264_qsv", "-c:v h264_qsv -preset medium -global_quality 20"),
                ("h264_amf", "-c:v h264_amf -quality quality -rc cqp -qp_i 19 -qp_p 21 -qp_b 23"),
            },
            "hevc" => new[]
            {
                ("hevc_nvenc", "-c:v hevc_nvenc -preset p5 -rc vbr -cq 24 -b:v 0 -tag:v hvc1"),
                ("hevc_qsv", "-c:v hevc_qsv -preset medium -global_quality 24 -tag:v hvc1"),
                ("hevc_amf", "-c:v hevc_amf -quality quality -rc cqp -qp_i 24 -qp_p 26 -qp_b 28 -tag:v hvc1"),
            },
            "av1" => new[]
            {
                ("av1_nvenc", "-c:v av1_nvenc -preset p5 -rc vbr -cq 32 -b:v 0"),
                ("av1_qsv", "-c:v av1_qsv -preset medium -global_quality 32"),
                ("av1_amf", "-c:v av1_amf -quality quality -rc cqp -qp_i 32 -qp_p 34"),
            },
            _ => Array.Empty<(string, string)>(),
        };

        foreach (var (encoder, args) in candidates)
        {
            if (await EncoderWorksAsync(encoder)) return args;
        }
        return CodecTranscodeArgs(codec);
    }

    /// <summary>Encodes a few frames of a synthetic clip to null; exit code 0 = the encoder and its driver work.</summary>
    private async Task<bool> EncoderWorksAsync(string encoder)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = FfmpegExecutable() ?? "ffmpeg.exe",
                Arguments = $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:r=30:d=0.5 -frames:v 10 -c:v {encoder} -f null -",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null) return false;
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Full path of the bundled ffmpeg.exe, or null when it is resolved from PATH.</summary>
    private string? FfmpegExecutable() =>
        string.IsNullOrWhiteSpace(_ffmpegDirectory) ? null : System.IO.Path.Combine(_ffmpegDirectory, "ffmpeg.exe");
}

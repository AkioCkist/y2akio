using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Windows.Storage;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>FFmpeg execution, hardware selection, progress parsing, and argument construction.</summary>
public sealed partial class MediaCompressPage : Page
{
    private async Task RunFFmpegCompressionAsync(string input, string output, bool isVideo, CancellationToken token)
    {
        var ffmpegPath = ResolveFFmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            throw new Exception("FFmpeg executable not found. Please verify bin directory.");

        string codecTag = (VideoCodecComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "hevc";
        string hwTag = (HwAccelComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";

        if (isVideo)
        {
            // Bước 1: Auto-Detect hãng GPU
            if (hwTag == "auto")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (StatusText != null)
                    {
                        StatusText.Text = "Detecting GPU capabilities...";
                        StatusText.Visibility = Visibility.Visible;
                    }
                    if (ProgressBar != null)
                    {
                        ProgressBar.Visibility = Visibility.Visible;
                        ProgressBar.IsIndeterminate = true; // Hiển thị thanh chạy vô định
                    }
                });
                hwTag = await DetectBestHwEncoderAsync();

                // Update status sau khi detect xong
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (StatusText != null)
                        StatusText.Text = $"GPU detected: {hwTag.ToUpper()}";
                });
            }

            // Bước 2: Chạy thử bằng Hardware Acceleration (Nếu không phải CPU)
            if (hwTag != "cpu")
            {
                string hwArgs = BuildFFmpegArgs(input, output, true, codecTag, hwTag);
                bool hwSuccess = await ExecuteFFmpegProcessAsync(ffmpegPath, hwArgs, token);

                if (hwSuccess) return; // GPU nén thành công, kết thúc!

                // Bước 3: Thuật toán GPU thất bại
                if (File.Exists(output)) File.Delete(output);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (StatusText != null)
                        StatusText.Text = "GPU encode unsupported. Falling back to CPU...";
                });

                hwTag = "cpu";
            }
        }

        // Chạy nén bằng CPU
        string finalArgs = BuildFFmpegArgs(input, output, isVideo, codecTag, hwTag);
        bool cpuSuccess = await ExecuteFFmpegProcessAsync(ffmpegPath, finalArgs, token);

        if (!cpuSuccess)
            throw new Exception("FFmpeg compression failed even on CPU.");
    }

    private async Task<bool> ExecuteFFmpegProcessAsync(string ffmpegPath, string args, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true // Thêm để bắt cả output nếu có
            },
            EnableRaisingEvents = true
        };

        // Đảm bảo Terminal Panel hiển thị trước khi start
        DispatcherQueue.TryEnqueue(() =>
        {
            if (TerminalPanel != null)
                TerminalPanel.Visibility = Visibility.Visible;
            if (ProgressBar != null)
            {
                ProgressBar.Visibility = Visibility.Visible;
                ProgressBar.IsIndeterminate = true;
            }
            if (StatusText != null)
            {
                StatusText.Visibility = Visibility.Visible;
                StatusText.Text = "Starting FFmpeg...";
            }
            if (LogTextBlock != null)
                LogTextBlock.Text = "--- STARTING FFMPEG ENGINE ---\n";
        });

        process.Start();

        TimeSpan totalDuration = TimeSpan.Zero;
        bool hasLoggedFirstLine = false;

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (!process.StandardError.EndOfStream && !token.IsCancellationRequested)
                {
                    var line = await process.StandardError.ReadLineAsync();
                    if (string.IsNullOrEmpty(line)) continue;

                    // Log lần đầu tiên
                    if (!hasLoggedFirstLine)
                    {
                        hasLoggedFirstLine = true;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (StatusText != null)
                                StatusText.Text = "FFmpeg is processing...";
                        });
                    }

                    // Cập nhật Terminal Log và tự động cuộn xuống cuối
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (LogTextBlock != null)
                        {
                            LogTextBlock.Text += line + "\n";
                        }
                        // Scroll xuống cuối
                        if (LogScrollViewer != null)
                        {
                            LogScrollViewer.UpdateLayout();
                            LogScrollViewer.ScrollToVerticalOffset(LogScrollViewer.ScrollableHeight);
                        }
                    });

                    // Phân tích tổng thời gian file (Duration)
                    if (totalDuration == TimeSpan.Zero && line.Contains("Duration: "))
                    {
                        var dur = ParseDuration(line);
                        if (dur.HasValue)
                        {
                            totalDuration = dur.Value;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (StatusText != null)
                                    StatusText.Text = $"Duration: {totalDuration:hh\\:mm\\:ss} - Compressing...";
                            });
                        }
                    }

                    // Phân tích thời gian đã nén (time=) để tính phần trăm
                    if (line.Contains("time="))
                    {
                        var currentTime = ParseTime(line);
                        if (currentTime.HasValue && totalDuration.TotalSeconds > 0)
                        {
                            double progress = (currentTime.Value.TotalSeconds / totalDuration.TotalSeconds) * 100;
                            progress = Math.Clamp(progress, 0, 100);

                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (ProgressBar != null)
                                {
                                    ProgressBar.IsIndeterminate = false;
                                    ProgressBar.Value = progress;
                                }
                                if (StatusText != null)
                                {
                                    StatusText.Text = $"Compressing... {progress:0.0}% ({currentTime.Value:hh\\:mm\\:ss} / {totalDuration:hh\\:mm\\:ss})";
                                }
                            });
                        }
                        else if (currentTime.HasValue)
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (ProgressBar != null)
                                    ProgressBar.IsIndeterminate = true;
                                if (StatusText != null)
                                    StatusText.Text = $"Compressing... {currentTime.Value:hh\\:mm\\:ss}";
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (LogTextBlock != null)
                        LogTextBlock.Text += $"\n--- ERROR: {ex.Message} ---\n";
                });
            }
        }, CancellationToken.None);

        // Drain stdout concurrently so a full pipe can never block FFmpeg. The cancellation
        // registration owns process termination; both readers are awaited before disposal.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        using var cancellationRegistration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between HasExited and Kill.
            }
        });

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Wait until the cancellation callback has finished terminating FFmpeg before the
            // Process and its redirected streams are disposed.
            try { await process.WaitForExitAsync(); } catch { /* best-effort shutdown */ }
            try { await stderrTask; } catch { /* stream closed during cancellation */ }
            try { await stdoutTask; } catch (OperationCanceledException) { }
            throw;
        }

        await stderrTask;

        // Đọc thêm output nếu có (phòng trường hợp FFmpeg ghi vào stdout)
        string additionalOutput = await stdoutTask;
        if (!string.IsNullOrEmpty(additionalOutput))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (LogTextBlock != null)
                    LogTextBlock.Text += additionalOutput + "\n";
            });
        }

        return process.ExitCode == 0;
    }

    private async Task<string> DetectBestHwEncoderAsync()
    {
        try
        {
            // Dùng PowerShell tích hợp gọi Windows WMI để đọc hãng card màn hình
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_VideoController).Name\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            output = output.ToLower();
            if (output.Contains("nvidia")) return "nvenc";
            if (output.Contains("amd") || output.Contains("radeon")) return "amf";
            if (output.Contains("intel")) return "qsv";
        }
        catch { }

        return "cpu"; // Nếu lỗi nhận diện, trả thẳng về CPU cho an toàn
    }

    private string BuildFFmpegArgs(string input, string output, bool isVideo, string codecTag, string hwTag)
    {
        if (isVideo)
        {
            int quality = (int)(VideoQualitySlider?.Value ?? 28);
            string vcodec = "";
            string extraArgs = "";

            // Mapping thuật toán Codec chéo với Hãng GPU
            if (codecTag == "av1")
            {
                switch (hwTag)
                {
                    case "nvenc": vcodec = "av1_nvenc"; extraArgs = $"-rc vbr -cq {quality} -preset p6"; break;
                    case "qsv": vcodec = "av1_qsv"; extraArgs = $"-global_quality {quality}"; break;
                    case "amf": vcodec = "av1_amf"; extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default: vcodec = "libsvtav1"; extraArgs = $"-crf {quality} -preset 6"; break;
                }
            }
            else if (codecTag == "hevc")
            {
                switch (hwTag)
                {
                    case "nvenc": vcodec = "hevc_nvenc"; extraArgs = $"-rc vbr -cq {quality} -preset p6"; break;
                    case "qsv": vcodec = "hevc_qsv"; extraArgs = $"-global_quality {quality}"; break;
                    case "amf": vcodec = "hevc_amf"; extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default: vcodec = "libx265"; extraArgs = $"-crf {quality} -preset slow"; break;
                }
            }
            else // h264
            {
                switch (hwTag)
                {
                    case "nvenc": vcodec = "h264_nvenc"; extraArgs = $"-rc vbr -cq {quality} -preset p6"; break;
                    case "qsv": vcodec = "h264_qsv"; extraArgs = $"-global_quality {quality}"; break;
                    case "amf": vcodec = "h264_amf"; extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default: vcodec = "libx264"; extraArgs = $"-crf {quality} -preset slow"; break;
                }
            }

            // Ép Pixel Format yuv420p để tương thích tuyệt đối mọi thiết bị khi render bằng GPU
            return $"-i \"{input}\" -c:v {vcodec} {extraArgs} -pix_fmt yuv420p -c:a aac -b:a 128k -y \"{output}\"";
        }
        else
        {
            var formatTag = (ImageFormatComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "webp";
            int quality = (int)(ImageQualitySlider?.Value ?? 80);

            return formatTag switch
            {
                "webp" => $"-i \"{input}\" -c:v libwebp -q:v {quality} -y \"{output}\"",
                "jpeg" => $"-i \"{input}\" -qscale:v {Math.Max(2, 31 - (int)(quality * 0.31))} -y \"{output}\"",
                "png" => $"-i \"{input}\" -c:v png -compression_level {quality} -y \"{output}\"",
                "gif" => $"-i \"{input}\" -c:v gif -y \"{output}\"",
                "bmp" => $"-i \"{input}\" -c:v bmp -y \"{output}\"",
                _ => $"-i \"{input}\" -y \"{output}\""
            };
        }
    }

    // ─── Helpers & Event Handlers ──────────────────────────────────────────

}

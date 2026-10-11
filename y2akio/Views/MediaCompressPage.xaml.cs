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

/// <summary>
/// Trang nén hình ảnh và video sử dụng FFmpeg engine với bộ thuật toán tối ưu.
/// </summary>
public sealed partial class MediaCompressPage : Page
{
    private string _selectedFilePath = string.Empty;
    private string _saveFolderPath = string.Empty;
    private long _originalSizeBytes = 0;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isProcessing;

    private readonly string[] _imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".gif" };
    private readonly string[] _videoExtensions = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv" };

    public MediaCompressPage()
    {
        InitializeComponent();

        try
        {
            // Thiết lập thư mục lưu mặc định từ AppSettingsService
            _saveFolderPath = AppSettingsService.Instance.VideoSaveFolder;
            if (string.IsNullOrEmpty(_saveFolderPath) || !Directory.Exists(_saveFolderPath))
            {
                _saveFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            }

            if (SaveLocationTextBox != null)
            {
                SaveLocationTextBox.Text = _saveFolderPath;
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Initialization error: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ─── Lựa chọn loại Media (Video / Image) ─────────────────────────────────

    private void MediaTypeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (VideoOptionsPanel == null || ImageOptionsPanel == null) return;

        // Tránh cảnh báo so sánh tham chiếu trên ToggleButton
        bool isVideo = sender is ToggleButton btn && btn == VideoFormatToggle;

        if (VideoFormatToggle != null) VideoFormatToggle.IsChecked = isVideo;
        if (ImageFormatToggle != null) ImageFormatToggle.IsChecked = !isVideo;

        VideoOptionsPanel.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
        ImageOptionsPanel.Visibility = isVideo ? Visibility.Collapsed : Visibility.Visible;

        ClearPreview();
    }

    // ─── Browse & Drag-and-Drop File ─────────────────────────────────────────

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            bool isVideo = VideoFormatToggle?.IsChecked == true;
            var extensions = isVideo ? _videoExtensions : _imageExtensions;

            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.Downloads;

            foreach (var ext in extensions)
            {
                picker.FileTypeFilter.Add(ext);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                await LoadMediaPreviewAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Cannot open file: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count > 0 && items[0] is StorageFile file)
                {
                    var ext = Path.GetExtension(file.Path).ToLower();

                    if (_videoExtensions.Contains(ext))
                    {
                        MediaTypeToggle_Click(VideoFormatToggle, e);
                    }
                    else if (_imageExtensions.Contains(ext))
                    {
                        MediaTypeToggle_Click(ImageFormatToggle, e);
                    }
                    else
                    {
                        ShowInfo("Format not supported for compression.", InfoBarSeverity.Warning);
                        return;
                    }

                    await LoadMediaPreviewAsync(file.Path);
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Failed to drop file: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ─── Preview & Info Loaders ──────────────────────────────────────────────

    private async Task LoadMediaPreviewAsync(string filePath)
    {
        _selectedFilePath = filePath;
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) return;

        _originalSizeBytes = fileInfo.Length;

        if (OriginalInfo != null)
            OriginalInfo.Text = FormatFileSize(_originalSizeBytes);

        if (CompressedImage != null) CompressedImage.Source = null;
        if (CompressedInfo != null) CompressedInfo.Text = "Waiting to compress";

        bool isVideo = VideoFormatToggle?.IsChecked == true;

        try
        {
            if (!isVideo)
            {
                var bitmap = new BitmapImage();
                using (var stream = File.OpenRead(filePath))
                {
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                }
                if (OriginalImage != null) OriginalImage.Source = bitmap;
            }
            else
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(filePath);
                var thumbnail = await storageFile.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.VideosView, 300);
                if (thumbnail != null && OriginalImage != null)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    OriginalImage.Source = bitmap;
                }
            }
        }
        catch
        {
            // Lỗi khi load ảnh xem trước không ảnh hưởng đến luồng nén
        }

        if (InfoBar != null) InfoBar.IsOpen = false;
    }

    private void ClearPreview()
    {
        _selectedFilePath = string.Empty;
        _originalSizeBytes = 0;
        if (OriginalImage != null) OriginalImage.Source = null;
        if (CompressedImage != null) CompressedImage.Source = null;
        if (OriginalInfo != null) OriginalInfo.Text = "No file selected";
        if (CompressedInfo != null) CompressedInfo.Text = "Waiting...";
    }

    // ─── Core Compression Execution ──────────────────────────────────────────

    private async void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
        {
            ShowInfo("Please select a valid file first.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_saveFolderPath) || !Directory.Exists(_saveFolderPath))
        {
            ShowInfo("Please choose a valid output directory.", InfoBarSeverity.Warning);
            return;
        }

        bool isVideo = VideoFormatToggle?.IsChecked == true;
        string ext = GetOutputExtension(isVideo);
        string outputFileName = $"{Path.GetFileNameWithoutExtension(_selectedFilePath)}_compressed{ext}";
        string outputPath = Path.Combine(_saveFolderPath, outputFileName);

        if (File.Exists(outputPath))
        {
            var dialog = new ContentDialog
            {
                Title = "File Exists",
                Content = $"The file '{outputFileName}' already exists. Overwrite it?",
                PrimaryButtonText = "Overwrite",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
        }

        _isProcessing = true;
        _cancellationTokenSource = new CancellationTokenSource();

        SetUIProcessingState(true);

        try
        {
            await RunFFmpegCompressionAsync(_selectedFilePath, outputPath, isVideo, _cancellationTokenSource.Token);

            var newFileInfo = new FileInfo(outputPath);
            if (newFileInfo.Exists)
            {
                double savedPercent = (1 - (double)newFileInfo.Length / _originalSizeBytes) * 100;
                if (CompressedInfo != null)
                    CompressedInfo.Text = $"{FormatFileSize(newFileInfo.Length)} (saved {savedPercent:0.#}%)";

                if (!isVideo)
                {
                    var bitmap = new BitmapImage();
                    using var stream = File.OpenRead(outputPath);
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    if (CompressedImage != null) CompressedImage.Source = bitmap;
                }
            }

            ShowInfo($"Compression complete! Output saved to: {outputPath}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowInfo("Compression cancelled.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowInfo($"Error during compression: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _isProcessing = false;
            SetUIProcessingState(false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        if (StatusText != null) StatusText.Text = "Cancelling processing...";
    }


    private string ResolveFFmpegPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg-9.0.1-full_build", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return string.Empty;
    }

    private string GetOutputExtension(bool isVideo)
    {
        if (isVideo) return ".mp4";

        var formatTag = (ImageFormatComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "webp";
        return formatTag switch
        {
            "webp" => ".webp",
            "jpeg" => ".jpg",
            "png"  => ".png",
            "gif"  => ".gif",
            "bmp"  => ".bmp",
            _      => ".jpg"
        };
    }

    // ─── FFmpeg Processing Engine (GPU Auto-Detect & Fallback) ───────────────

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

        _ = Task.Run(async () =>
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
        }, token);

        // Chờ process kết thúc hoặc bị cancel
        while (!process.WaitForExit(100) && !token.IsCancellationRequested) 
        { 
            // Đợi
        }

        if (token.IsCancellationRequested)
        {
            try { process.Kill(); } catch { }
            token.ThrowIfCancellationRequested();
        }

        // Đọc thêm output nếu có (phòng trường hợp FFmpeg ghi vào stdout)
        string additionalOutput = await process.StandardOutput.ReadToEndAsync();
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
            process.WaitForExit();
            
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
                    case "qsv":   vcodec = "av1_qsv";   extraArgs = $"-global_quality {quality}"; break;
                    case "amf":   vcodec = "av1_amf";   extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default:      vcodec = "libsvtav1"; extraArgs = $"-crf {quality} -preset 6"; break;
                }
            }
            else if (codecTag == "hevc")
            {
                switch (hwTag)
                {
                    case "nvenc": vcodec = "hevc_nvenc"; extraArgs = $"-rc vbr -cq {quality} -preset p6"; break;
                    case "qsv":   vcodec = "hevc_qsv";   extraArgs = $"-global_quality {quality}"; break;
                    case "amf":   vcodec = "hevc_amf";   extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default:      vcodec = "libx265";    extraArgs = $"-crf {quality} -preset slow"; break;
                }
            }
            else // h264
            {
                switch (hwTag)
                {
                    case "nvenc": vcodec = "h264_nvenc"; extraArgs = $"-rc vbr -cq {quality} -preset p6"; break;
                    case "qsv":   vcodec = "h264_qsv";   extraArgs = $"-global_quality {quality}"; break;
                    case "amf":   vcodec = "h264_amf";   extraArgs = $"-rc cqp -qp_i {quality} -qp_p {quality}"; break;
                    default:      vcodec = "libx264";    extraArgs = $"-crf {quality} -preset slow"; break;
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
                "png"  => $"-i \"{input}\" -c:v png -compression_level {quality} -y \"{output}\"",
                "gif"  => $"-i \"{input}\" -c:v gif -y \"{output}\"",
                "bmp"  => $"-i \"{input}\" -c:v bmp -y \"{output}\"",
                _      => $"-i \"{input}\" -y \"{output}\""
            };
        }
    }

    // ─── Helpers & Event Handlers ──────────────────────────────────────────

    private void VideoQualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (VideoQualityValueText != null)
        {
            VideoQualityValueText.Text = $"{e.NewValue:0}";
        }
    }

    private void ImageQualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (ImageQualityValueText != null)
        {
            ImageQualityValueText.Text = $"{e.NewValue:0}%";
        }
    }

    private async void ChooseSaveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();

            if (folder != null)
            {
                _saveFolderPath = folder.Path;
                if (SaveLocationTextBox != null) SaveLocationTextBox.Text = _saveFolderPath;
            }
        }
        catch (Exception ex)
        {
            ShowInfo($"Could not select folder: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void SetUIProcessingState(bool isProcessing)
    {
        if (CompressButton != null) CompressButton.IsEnabled = !isProcessing;
        if (CancelButton != null) CancelButton.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
        if (TerminalPanel != null) TerminalPanel.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;

        if (ProgressBar != null)
        {
            ProgressBar.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = isProcessing; // Bật thanh chạy vô định khi đang Detect GPU
            ProgressBar.Value = 0;
        }

        if (StatusText != null)
        {
            StatusText.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = isProcessing ? "Initializing engine..." : string.Empty;
        }

        if (isProcessing && LogTextBlock != null)
        {
            LogTextBlock.Text = "--- STARTING FFMPEG ENGINE ---\n";
        }
    }

    // --- Các hàm hỗ trợ bóc tách log của FFmpeg ---

    private TimeSpan? ParseDuration(string line)
    {
        try
        {
            // Định dạng log FFmpeg: "  Duration: 00:03:15.12, start: 0.000000, bitrate: 1234 kb/s"
            int startIndex = line.IndexOf("Duration: ") + 10;
            int endIndex = line.IndexOf(",", startIndex);
            if (startIndex >= 10 && endIndex > startIndex)
            {
                string durationStr = line.Substring(startIndex, endIndex - startIndex).Trim();
                if (TimeSpan.TryParse(durationStr, out var ts)) return ts;
            }
        }
        catch { }
        return null;
    }

    private TimeSpan? ParseTime(string line)
    {
        try
        {
            // Định dạng log FFmpeg: "frame=  123 fps= 30 q=28.0 size= 1024kB time=00:00:10.50 bitrate=..."
            int startIndex = line.IndexOf("time=") + 5;
            int endIndex = line.IndexOf(" ", startIndex);
            if (endIndex == -1) endIndex = line.Length;
            
            if (startIndex >= 5 && endIndex > startIndex)
            {
                string timeStr = line.Substring(startIndex, endIndex - startIndex).Trim();
                if (TimeSpan.TryParse(timeStr, out var ts)) return ts;
            }
        }
        catch { }
        return null;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private void ShowInfo(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (InfoBar != null)
            {
                InfoBar.Severity = severity;
                InfoBar.Message = message;
                InfoBar.IsOpen = true;
            }
        });
    }
}
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

/// <summary>Compression control event handlers and shared UI helpers.</summary>
public sealed partial class MediaCompressPage : Page
{
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

    private static TimeSpan? ParseDuration(string line)
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

    private static TimeSpan? ParseTime(string line)
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

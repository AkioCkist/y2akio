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
using y2akio.Helpers;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Compression workflow, cancellation, and output handling.</summary>
public sealed partial class MediaCompressPage : Page
{
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


    private static string ResolveFFmpegPath() =>
        ExecutableLocator.Find(
            "ffmpeg.exe",
            Path.Combine("bin", "ffmpeg-9.0.1-full_build", "bin"),
            "bin",
            "") ?? string.Empty;

    private string GetOutputExtension(bool isVideo)
    {
        if (isVideo) return ".mp4";

        var formatTag = (ImageFormatComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "webp";
        return formatTag switch
        {
            "webp" => ".webp",
            "jpeg" => ".jpg",
            "png" => ".png",
            "gif" => ".gif",
            "bmp" => ".bmp",
            _ => ".jpg"
        };
    }

    // ─── FFmpeg Processing Engine (GPU Auto-Detect & Fallback) ───────────────

}

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

}

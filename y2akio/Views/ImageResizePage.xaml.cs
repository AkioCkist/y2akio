using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using y2akio;
using y2akio.Services;

namespace y2akio.Views;

/// <summary>Image Resize page: pick an image, set a target size/format, and resize it via ImageResizeService.</summary>
public sealed partial class ImageResizePage : Page
{
    private readonly ImageResizeService _service = new();
    private string? _sourcePath;
    private string _outputFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads";

    public ImageResizePage()
    {
        InitializeComponent();
        OutputFolderText.Text = _outputFolder;
    }

    private async void ChooseImageButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif", ".webp" })
            picker.FileTypeFilter.Add(ext);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        _sourcePath = file.Path;
        FileNameText.Text = Path.GetFileName(file.Path);
        PreviewImage.Source = new BitmapImage(new Uri(file.Path));
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        SourceDimsText.Text = "Loading...";

        OutputNameTextBox.Text = Path.GetFileNameWithoutExtension(file.Name) + "." + CurrentExtension();
        UpdateOutputNameExtension();
    }

    private void PreviewImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is BitmapImage bmp)
            SourceDimsText.Text = $"{bmp.PixelWidth} x {bmp.PixelHeight} px";
    }

    private async void ChooseOutputFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            _outputFolder = folder.Path;
            OutputFolderText.Text = _outputFolder;
        }
    }

    private void ModeRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before all fields are wired; skip until layout done.
        if (PixelPanel is null || PercentPanel is null) return;
        var isPercent = ModeRadio.SelectedIndex == 1;
        PixelPanel.Visibility = isPercent ? Visibility.Collapsed : Visibility.Visible;
        PercentPanel.Visibility = isPercent ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FormatComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Fires during InitializeComponent before all fields are wired; skip until layout done.
        if (QualityRow is null || OutputNameTextBox is null) return;
        var isJpegOrGif = CurrentFormat() is ImageResizeFormat.Jpeg or ImageResizeFormat.Gif;
        QualityRow.Visibility = isJpegOrGif ? Visibility.Visible : Visibility.Collapsed;
        UpdateOutputNameExtension();
    }

    private void QualitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Fires during InitializeComponent before QualityValueText is wired.
        if (QualityValueText is null) return;
        QualityValueText.Text = ((int)Math.Round(e.NewValue)).ToString();
    }

    private void OutputNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // No-op: extension is normalized at resize time / format change.
    }

    private void UpdateOutputNameExtension()
    {
        var name = OutputNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        OutputNameTextBox.Text = Path.GetFileNameWithoutExtension(name) + "." + CurrentExtension();
    }

    private ImageResizeFormat CurrentFormat() =>
        (FormatComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "jpg" => ImageResizeFormat.Jpeg,
            "bmp" => ImageResizeFormat.Bmp,
            "tiff" => ImageResizeFormat.Tiff,
            "gif" => ImageResizeFormat.Gif,
            _ => ImageResizeFormat.Png,
        };

    private string CurrentExtension() => CurrentFormat() switch
    {
        ImageResizeFormat.Jpeg => "jpg",
        ImageResizeFormat.Bmp => "bmp",
        ImageResizeFormat.Tiff => "tiff",
        ImageResizeFormat.Gif => "gif",
        _ => "png",
    };

    private async void ResizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null)
        {
            ErrorBar.Message = "Choose a source image first.";
            ErrorBar.IsOpen = true;
            return;
        }

        int? width = null;
        int? height = null;
        double? scalePercent = null;

        if (ModeRadio.SelectedIndex == 1)
        {
            scalePercent = double.IsNaN(PercentScaleBox.Value) ? null : PercentScaleBox.Value;
            if (scalePercent is <= 0)
            {
                ErrorBar.Message = "Enter a valid scale percentage.";
                ErrorBar.IsOpen = true;
                return;
            }
        }
        else
        {
            width = ReadSize(TargetWidth.Value);
            height = ReadSize(TargetHeight.Value);
        }

        var name = OutputNameTextBox.Text?.Trim();
        if (string.IsNullOrEmpty(name)) name = $"resized_{DateTime.Now:yyyyMMdd_HHmmss}.{CurrentExtension()}";
        name = Path.GetFileNameWithoutExtension(name) + "." + CurrentExtension();
        var outputPath = Path.Combine(_outputFolder, name);

        ResultBar.IsOpen = false;
        ErrorBar.IsOpen = false;
        ResizeButton.IsEnabled = false;
        ResizeRing.IsActive = true;

        try
        {
            var result = await _service.ResizeAsync(
                _sourcePath, outputPath, width, height, scalePercent,
                KeepAspectCheck.IsChecked == true, CurrentFormat(), QualitySlider.Value);

            var sizeKb = Math.Max(1, result.BytesWritten / 1024);
            ResultBar.Title = "Resize complete";
            ResultBar.Message =
                $"{result.SourceWidth}x{result.SourceHeight} → {result.OutputWidth}x{result.OutputHeight} " +
                $"({sizeKb} KB) saved to {result.OutputPath}";

            var openBtn = new HyperlinkButton { Content = "Open folder" };
            openBtn.Click += (_, _) => _ = Windows.System.Launcher.LaunchFolderPathAsync(_outputFolder);
            ResultBar.ActionButton = openBtn;

            ResultBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            ResizeButton.IsEnabled = true;
            ResizeRing.IsActive = false;
        }
    }

    private static int? ReadSize(double value) =>
        double.IsNaN(value) || value <= 0 ? null : (int)Math.Round(value);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using Windows.Storage;

namespace y2akio.Services;

/// <summary>Output formats supported by the image resizer (all built into Windows WIC).</summary>
public enum ImageResizeFormat { Png, Jpeg, Bmp, Tiff, Gif }

/// <summary>Result of a completed resize.</summary>
public sealed class ImageResizeResult
{
    public required string OutputPath { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required int OutputWidth { get; init; }
    public required int OutputHeight { get; init; }
    public long BytesWritten { get; init; }
}

/// <summary>
/// Resizes images using the Windows Imaging Component (WIC) via WinRT — no external packages.
/// Decodes the source with <see cref="BitmapDecoder"/>, scales it with a <see cref="BitmapTransform"/>
/// (Fant interpolation), then encodes to PNG/JPEG/BMP/TIFF/GIF with <see cref="BitmapEncoder"/>.
/// </summary>
public sealed class ImageResizeService
{
    private static readonly Dictionary<ImageResizeFormat, Guid> EncoderIds = new()
    {
        [ImageResizeFormat.Png] = BitmapEncoder.PngEncoderId,
        [ImageResizeFormat.Jpeg] = BitmapEncoder.JpegEncoderId,
        [ImageResizeFormat.Bmp] = BitmapEncoder.BmpEncoderId,
        [ImageResizeFormat.Tiff] = BitmapEncoder.TiffEncoderId,
        [ImageResizeFormat.Gif] = BitmapEncoder.GifEncoderId,
    };

    /// <summary>
    /// Resizes <paramref name="sourcePath"/> to <paramref name="outputPath"/>.
    /// Two modes:
    /// <list type="bullet">
    /// <item><b>By pixel</b> — pass <paramref name="targetWidth"/>/<paramref name="targetHeight"/>; with
    /// <paramref name="keepAspectRatio"/> true only one dimension is required, the other is derived from the source.</item>
    /// <item><b>By percentage</b> — pass <paramref name="scalePercent"/> (e.g. 50 = half size, 200 = double); pixel params are ignored.</item>
    /// </list>
    /// Returns the output path and dimensions.
    /// </summary>
    public async Task<ImageResizeResult> ResizeAsync(
        string sourcePath,
        string outputPath,
        int? targetWidth,
        int? targetHeight,
        double? scalePercent,
        bool keepAspectRatio,
        ImageResizeFormat format,
        double jpegQuality = 92,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
            throw new InvalidOperationException("Source image not found.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new InvalidOperationException("Output path missing.");

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);

        // 1) Decode the source.
        var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath).AsTask(ct);
        using var sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(sourceStream).AsTask(ct);

        var srcW = (int)decoder.PixelWidth;
        var srcH = (int)decoder.PixelHeight;

        // 2) Compute the output dimensions.
        int outW, outH;
        if (scalePercent is double pct)
        {
            if (pct <= 0)
                throw new InvalidOperationException("Scale percentage must be greater than 0.");
            outW = Math.Max(1, (int)Math.Round(srcW * pct / 100.0));
            outH = Math.Max(1, (int)Math.Round(srcH * pct / 100.0));
        }
        else if (keepAspectRatio)
        {
            if (targetWidth is int w && w > 0)
            {
                outW = w;
                outH = Math.Max(1, (int)Math.Round(srcH * w / (double)srcW));
            }
            else if (targetHeight is int h && h > 0)
            {
                outH = h;
                outW = Math.Max(1, (int)Math.Round(srcW * h / (double)srcH));
            }
            else
            {
                throw new InvalidOperationException("Enter a target width or height (aspect ratio is on).");
            }
        }
        else
        {
            if (!(targetWidth is int tw && tw > 0) || !(targetHeight is int th && th > 0))
                throw new InvalidOperationException("Aspect ratio is off — enter both width and height.");
            outW = tw;
            outH = th;
        }

        ct.ThrowIfCancellationRequested();

        // 3) Scale while decoding (Fant = high-quality bicubic).
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)outW,
            ScaledHeight = (uint)outH,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct);

        // 4) Encode to the requested format.
        using var fs = File.Create(outputPath);
        using var outputStream = fs.AsRandomAccessStream();

        var encoder = await BitmapEncoder.CreateAsync(EncoderIds[format], outputStream).AsTask(ct);

        if (format is ImageResizeFormat.Jpeg or ImageResizeFormat.Gif)
        {
            var quality = Math.Clamp(jpegQuality, 1, 100) / 100.0;
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue((float)quality, PropertyType.Single) },
            };
            await encoder.BitmapProperties.SetPropertiesAsync(props).AsTask(ct);
        }

        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            (uint)outW,
            (uint)outH,
            decoder.DpiX,
            decoder.DpiY,
            pixelData.DetachPixelData());
        await encoder.FlushAsync().AsTask(ct);

        return new ImageResizeResult
        {
            OutputPath = outputPath,
            SourceWidth = srcW,
            SourceHeight = srcH,
            OutputWidth = outW,
            OutputHeight = outH,
            BytesWritten = new FileInfo(outputPath).Length,
        };
    }
}

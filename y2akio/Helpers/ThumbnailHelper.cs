using System;
using Microsoft.UI.Xaml.Media.Imaging;

namespace y2akio.Helpers;

/// <summary>Converts a thumbnail URL string into a BitmapImage for x:Bind in a DataTemplate.</summary>
public static class ThumbnailHelper
{
    public static BitmapImage? ToBitmap(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        return new BitmapImage(uri);
    }
}

using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using y2akio.Services.Downloads;

namespace y2akio.Helpers;

/// <summary>
/// IDM-style segment bar: the file as a horizontal track, with the downloaded part of every segment
/// filled in. Segments a connection is working on right now use the accent color, finished or
/// paused ones a dimmer fill, so you can watch the connections split the file.
/// </summary>
public sealed class SegmentBar : Grid
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<SegmentSnapshot>), typeof(SegmentBar),
        new PropertyMetadata(null, (d, _) => ((SegmentBar)d).Redraw()));

    public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
        nameof(Total), typeof(long), typeof(SegmentBar),
        new PropertyMetadata(0L, (d, _) => ((SegmentBar)d).Redraw()));

    private readonly Canvas _canvas = new();
    private readonly Rectangle _track = new() { RadiusX = 2, RadiusY = 2 };

    public SegmentBar()
    {
        Height = 6;
        Children.Add(_track);
        Children.Add(_canvas);
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public IReadOnlyList<SegmentSnapshot>? Segments
    {
        get => (IReadOnlyList<SegmentSnapshot>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public long Total
    {
        get => (long)GetValue(TotalProperty);
        set => SetValue(TotalProperty, value);
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var segments = Segments;
        var total = Total;
        Visibility = segments is { Count: > 0 } && total > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Visibility == Visibility.Collapsed || ActualWidth <= 0) return;

        _track.Fill = Brush("ControlStrongFillColorDisabledBrush");
        var active = Brush("AccentFillColorDefaultBrush");
        var idle = Brush("AccentFillColorTertiaryBrush");
        var width = ActualWidth;

        foreach (var s in segments!)
        {
            var done = s.Position - s.Start;
            if (done <= 0) continue;
            var x = s.Start * width / total;
            var w = System.Math.Max(1, done * width / total);
            var rect = new Rectangle { Width = w, Height = Height, Fill = s.Active ? active : idle };
            Canvas.SetLeft(rect, x);
            _canvas.Children.Add(rect);

            if (s.Active)
            {
                // Thin marker at each connection's write position.
                var marker = new Rectangle { Width = 2, Height = Height, Fill = Brush("TextFillColorPrimaryBrush") };
                Canvas.SetLeft(marker, System.Math.Min(width - 2, x + w));
                _canvas.Children.Add(marker);
            }
        }
    }

    private Brush Brush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
}

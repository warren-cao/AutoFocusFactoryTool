using System.Windows;
using System.Windows.Controls;

namespace FocusIR.Workbench.App.Controls;

/// <summary>按视频实际比例安排原生播放窗口，避免播放窗口自身出现大片黑边。</summary>
public sealed class VideoAspectBox : Decorator
{
    public static readonly DependencyProperty AspectRatioProperty = DependencyProperty.Register(
        nameof(AspectRatio), typeof(double), typeof(VideoAspectBox),
        new FrameworkPropertyMetadata(16.0 / 9, FrameworkPropertyMetadataOptions.AffectsMeasure
            | FrameworkPropertyMetadataOptions.AffectsArrange),
        value => value is double ratio && double.IsFinite(ratio) && ratio > 0);

    public double AspectRatio
    {
        get => (double)GetValue(AspectRatioProperty);
        set => SetValue(AspectRatioProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var size = Fit(constraint);
        Child?.Measure(size);
        return size;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        var size = Fit(arrangeSize);
        Child?.Arrange(new Rect((arrangeSize.Width - size.Width) / 2,
            (arrangeSize.Height - size.Height) / 2, size.Width, size.Height));
        return arrangeSize;
    }

    private Size Fit(Size available)
    {
        var width = available.Width;
        var height = available.Height;
        if (double.IsPositiveInfinity(width) && double.IsPositiveInfinity(height)) width = 640;
        if (double.IsPositiveInfinity(width)) width = height * AspectRatio;
        if (double.IsPositiveInfinity(height)) height = width / AspectRatio;
        width = Math.Max(0, Math.Min(width, height * AspectRatio));
        return new Size(width, width / AspectRatio);
    }
}

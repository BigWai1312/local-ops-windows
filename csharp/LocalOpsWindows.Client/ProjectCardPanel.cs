using System.Windows;
using System.Windows.Controls;

namespace LocalOpsWindows.Client;

public sealed class ProjectCardPanel : WrapPanel
{
    public static readonly DependencyProperty MinimumCardWidthProperty = DependencyProperty.Register(
        nameof(MinimumCardWidth), typeof(double), typeof(ProjectCardPanel),
        new FrameworkPropertyMetadata(320d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumCardWidth { get => (double)GetValue(MinimumCardWidthProperty); set => SetValue(MinimumCardWidthProperty, value); }
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            var columns = Math.Max(1, (int)(availableSize.Width / Math.Max(1, MinimumCardWidth)));
            ItemWidth = availableSize.Width / columns;
        }
        return base.MeasureOverride(availableSize);
    }
}

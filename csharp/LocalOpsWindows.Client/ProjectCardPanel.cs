using System.Windows;
using System.Windows.Controls;

namespace LocalOpsWindows.Client;

public sealed class ProjectCardPanel : WrapPanel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            var columns = Math.Max(1, (int)(availableSize.Width / 310));
            ItemWidth = availableSize.Width / columns;
        }
        return base.MeasureOverride(availableSize);
    }
}

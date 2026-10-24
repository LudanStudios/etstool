using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ETSAssistant.WinUI3.Services;

public static class ResponsiveLayoutService
{
    private const double CompactContentWidth = 900;
    private const double WideContentWidth = 980;
    // Reserve the NavigationView toggle row when its pane collapses over page content.
    private const double CompactTopInset = 58;

    public static void Attach(Page page)
    {
        if (page.FindName("ResponsiveContent") is not FrameworkElement content)
            return;

        Thickness originalMargin = content.Margin;
        bool? isCompact = null;

        void Update(double width)
        {
            if (width <= 0) return;

            bool compact = isCompact ?? width < CompactContentWidth;
            if (width < CompactContentWidth) compact = true;
            else if (width > WideContentWidth) compact = false;
            if (isCompact == compact) return;

            isCompact = compact;
            content.Margin = compact
                ? new Thickness(
                    Math.Min(originalMargin.Left, 18),
                    Math.Max(originalMargin.Top, CompactTopInset),
                    Math.Min(originalMargin.Right, 18),
                    Math.Min(originalMargin.Bottom, 20))
                : originalMargin;
        }

        page.SizeChanged += (_, args) => Update(args.NewSize.Width);
        page.Loaded += (_, _) => Update(page.ActualWidth);
        Update(page.ActualWidth);
    }
}

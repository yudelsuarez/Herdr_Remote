using Microsoft.Maui.Layouts;

namespace HerdrRemote.Pages;

public sealed class PaneOverlayScroll : ScrollView
{
    private double _measuredWidth = double.NaN;
    private double _measuredHeight;

    protected override Size MeasureOverride(double widthConstraint, double heightConstraint)
    {
        Size size = base.MeasureOverride(widthConstraint, heightConstraint);
        _measuredWidth = widthConstraint;
        _measuredHeight = size.Height;
        return size;
    }

    protected override Size ArrangeOverride(Rect bounds)
    {
        Rect frame = this.ComputeFrame(bounds);
        // A split can narrow the cell without a new measure. Size the scrolling content to the actual viewport.
        if (_measuredWidth != frame.Width) { MeasureOverride(frame.Width, bounds.Height); }
        frame.Height = Math.Min(_measuredHeight, Math.Max(0, bounds.Height));
        Frame = frame;
        Handler?.PlatformArrange(Frame);
        return Frame.Size;
    }
}

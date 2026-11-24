using Microsoft.Maui.Layouts;

namespace HerdrRemote.Pages;

public sealed class PaneOverlayScroll : ScrollView
{
    protected override Size ArrangeOverride(Rect bounds)
    {
        Rect frame = this.ComputeFrame(bounds);
        // Start keeps its measured height; the viewport must fit its final cell.
        frame.Height = Math.Min(frame.Height, Math.Max(0, bounds.Height));
        Frame = frame;
        Handler?.PlatformArrange(Frame);
        return Frame.Size;
    }
}

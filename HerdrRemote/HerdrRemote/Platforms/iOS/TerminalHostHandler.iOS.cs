using HerdrRemote.Pages;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using UIKit;
using Foundation;
using ObjCRuntime;
using NativeContentView = Microsoft.Maui.Platform.ContentView;

namespace HerdrRemote;

public sealed class TerminalHostHandler : ContentViewHandler
{
	private static readonly IPropertyMapper<IContentView, IContentViewHandler> HostMapper =
		new PropertyMapper<IContentView, IContentViewHandler>(ContentViewHandler.Mapper) { [nameof(IContentView.Content)] = MapTerminal };
	private TerminalHost? _host;

	public TerminalHostHandler() : base(HostMapper) { }

	private sealed class NativeHost : NativeContentView
	{
		internal Action<bool>? Changed;
		internal TerminalView? Terminal;
		public override bool CanPerform(Selector action, NSObject? sender) => action.Name == "paste:"
			? Terminal?.CanNativePaste == true : base.CanPerform(action, sender);
		public override NSObject? GetTargetForAction(Selector action, NSObject? sender) => action.Name == "paste:" && Terminal?.CanNativePaste == true
			? this : base.GetTargetForAction(action, sender);
		public override void Paste(NSObject? sender) => Terminal?.CaptureNativePaste();
		public override void MovedToWindow() { base.MovedToWindow(); Changed?.Invoke(false); SetNeedsLayout(); }
		public override void LayoutSubviews() { base.LayoutSubviews(); Changed?.Invoke(true); }
	}

	protected override NativeContentView CreatePlatformView() => new NativeHost { CrossPlatformLayout = VirtualView };

	private static void MapTerminal(IContentViewHandler handler, IContentView view)
	{
		TerminalHostHandler self = (TerminalHostHandler)handler;
		TerminalHost host = self._host = (TerminalHost)view;
		IMauiContext context = host.RetainedContext ?? throw new InvalidOperationException("No terminal owner context.");
		NativeHost native = (NativeHost)self.PlatformView;
		native.Terminal = view.PresentedContent as TerminalView;
		native.ClearSubviews();
		host.Map(self, () => native.Window is not null && native.Subviews.Length != 0, () =>
		{
			native.ClearSubviews();
			host.NativeChanged(self);
		});
		native.Changed = arranged => host.NativeChanged(self, arranged);
		if (view.PresentedContent is { } content)
		{
			UIView child = content.ToPlatform(context);
			child.RemoveFromSuperview();
			native.AddSubview(child);
			if (content.FlowDirection == Microsoft.Maui.FlowDirection.MatchParent) { child.UpdateFlowDirection(content); }
			InvalidateAncestors(child);
		}
		else if (native.Bounds.Width > 0 || native.Bounds.Height > 0) { native.InvalidateMeasure(view); }
		native.SetNeedsLayout();
	}

	// Stock ContentViewHandler's ancestor propagation, through its public controller contract (the extension itself is internal).
	private static void InvalidateAncestors(UIView child)
	{
		IPlatformMeasureInvalidationController? controller = child as IPlatformMeasureInvalidationController;
		while (true)
		{
			if (controller is not null && child.Window is null) { controller.InvalidateAncestorsMeasuresWhenMovedToWindow(); break; }
			if (child.Superview is not { } parent) { break; }
			IPlatformMeasureInvalidationController? next = parent as IPlatformMeasureInvalidationController;
			if (next is null) { parent.SetNeedsLayout(); }
			else if (!next.InvalidateMeasure(isPropagating: true)) { break; }
			child = parent;
			controller = next;
		}
	}

	protected override void DisconnectHandler(NativeContentView platformView)
	{
		((NativeHost)platformView).Changed = null;
		((NativeHost)platformView).Terminal = null;
		platformView.ClearSubviews();
		_host?.Unmap(this);
		_host = null;
		base.DisconnectHandler(platformView);
	}
}

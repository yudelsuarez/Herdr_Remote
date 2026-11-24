using HerdrRemote.Pages;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace HerdrRemote;

public sealed class TerminalHostHandler : ContentViewHandler
{
	private static readonly IPropertyMapper<IContentView, IContentViewHandler> HostMapper =
		new PropertyMapper<IContentView, IContentViewHandler>(ContentViewHandler.Mapper) { [nameof(IContentView.Content)] = MapTerminal };
	private TerminalHost? _host;

	public TerminalHostHandler() : base(HostMapper) { }

	private sealed class NativeHost(Android.Content.Context context) : ContentViewGroup(context)
	{
		internal Action<bool>? Changed;
		protected override void OnAttachedToWindow() { base.OnAttachedToWindow(); Changed?.Invoke(false); RequestLayout(); }
		protected override void OnDetachedFromWindow() { base.OnDetachedFromWindow(); Changed?.Invoke(false); }
		protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
		{
			base.OnLayout(changed, left, top, right, bottom);
			Changed?.Invoke(true);
		}
	}

	protected override ContentViewGroup CreatePlatformView()
	{
		NativeHost native = new(Context) { CrossPlatformLayout = VirtualView };
		native.SetClipChildren(false);
		return native;
	}

	private static void MapTerminal(IContentViewHandler handler, IContentView view)
	{
		TerminalHostHandler self = (TerminalHostHandler)handler;
		TerminalHost host = self._host = (TerminalHost)view;
		IMauiContext context = host.RetainedContext ?? throw new InvalidOperationException("No terminal owner context.");
		NativeHost native = (NativeHost)self.PlatformView;
		native.RemoveAllViews();
		host.Map(self, () => native.IsAttachedToWindow && native.ChildCount != 0, () =>
		{
			native.RemoveAllViews();
			host.NativeChanged(self);
		});
		native.Changed = arranged => host.NativeChanged(self, arranged);
		if (view.PresentedContent is { } content)
		{
			Android.Views.View child = content.ToPlatform(context);
			child.RemoveFromParent();
			native.AddView(child);
		}
		native.RequestLayout();
	}

	protected override void DisconnectHandler(ContentViewGroup platformView)
	{
		((NativeHost)platformView).Changed = null;
		platformView.RemoveAllViews();
		_host?.Unmap(this);
		_host = null;
		base.DisconnectHandler(platformView);
	}
}

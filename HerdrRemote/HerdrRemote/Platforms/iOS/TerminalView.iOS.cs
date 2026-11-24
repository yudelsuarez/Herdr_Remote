using System.Runtime.InteropServices;
using CoreGraphics;
using UIKit;
using HerdrRemote.Services;

namespace HerdrRemote.Pages;

// The selection's edit menu on iOS: the system's own (UIEditMenuInteraction) on the web view, at the selection's rectangle, with the
// page's actions as UIActions. The page's CSS pixels are the web view's points (the page is not zoomed or scrolled).
// UIEditMenuInteraction needs iOS 16, the app's minimum.
public sealed partial class TerminalView
{
	private UIEditMenuInteraction? _menuInteraction;
	private MenuDelegate? _menuDelegate;
	private UIView? _menuHost;
	private UIEditMenuConfiguration? _menuConfiguration;
	private int _menuGeneration;
	private (string Lease, int Id, int Generation)? _pasteContext;
	internal event Action<NativePastePayload>? NativePaste;

	internal sealed class NativePastePayload(TerminalView owner, string lease, int id, int generation,
		(bool Text, bool Image) kinds, string? text, (byte[] Data, string Extension)? image, Exception? error, bool rejected)
	{
		private bool _accepted;
		internal string Lease => lease;
		internal (bool Text, bool Image) Kinds => kinds;
		internal string? Text => text;
		internal (byte[] Data, string Extension)? Image => image;
		internal Exception? Error => error;
		internal bool Rejected => rejected;
		internal bool Current => owner.NativePasteCurrent && lease == owner._lease && generation == owner._menuGeneration;
		internal bool TryAccept()
		{
			if (_accepted || !Current || owner._menu?.Id != id || owner._pasteContext != (lease, id, generation)) { return false; }
			_accepted = true;
			owner._pasteContext = null;
			owner.ActOnMenu("clear", lease, id);
			owner._menu = null;
			owner.DismissMenu();
			return true;
		}
	}

	private bool NativePasteCurrent => !_released && !_documentFailed && IsReady && _leaseActive
		&& _menuHost?.Window is not null && Handler?.PlatformView == _menuHost;
	internal bool CanNativePaste => NativePasteCurrent && _menu is not null
		&& (ClipboardImage.Kinds() is var kinds && (kinds.Text || kinds.Image));

	internal void CaptureNativePaste()
	{
		if (!CanNativePaste || _pasteContext is not { } context || context != (_lease, _menu!.Id, _menuGeneration)) { return; }
		var payload = ReadPasteboard();
		NativePaste?.Invoke(new NativePastePayload(this, context.Lease, context.Id, context.Generation, payload.Kinds, payload.Text, payload.Image, payload.Error, payload.Rejected));
	}

	internal static ((bool Text, bool Image) Kinds, string? Text, (byte[] Data, string Extension)? Image, Exception? Error, bool Rejected) ReadPasteboard()
	{
		(bool Text, bool Image) kinds = default;
		string? text = null;
		(byte[] Data, string Extension)? image = null;
		Exception? error = null;
		bool rejected = false;
		try
		{
			UIPasteboard pasteboard = UIPasteboard.General;
			nint before = pasteboard.ChangeCount;
			kinds = ClipboardImage.Kinds();
			text = kinds.Text ? pasteboard.String : null;
			image = kinds.Image ? ClipboardImage.ReadNative() : null;
			rejected = pasteboard.ChangeCount != before;
		}
		catch (Exception ex) { error = ex; }
		return (kinds, text, image, error, rejected);
	}

	private void EnsureMenuFor(UIView view)
	{
		if (_released || (_menuHost == view && _menuInteraction is not null)) { return; }
		MenuHandlerChanging(_menuHost);
		_menuHost = view;
		_menuDelegate = new MenuDelegate(this, _menuGeneration);
		_menuInteraction = new UIEditMenuInteraction(_menuDelegate);
		view.AddInteraction(_menuInteraction);
	}

	partial void MenuHandlerChanging(object? native)
	{
		_menuGeneration++;
		_menu = null;
		_pasteContext = null;
		_menuConfiguration = null;
		UIEditMenuInteraction? interaction = _menuInteraction;
		_menuInteraction = null;
		if (interaction is not null)
		{
			interaction.DismissMenu();
			_menuHost?.RemoveInteraction(interaction);
			interaction.Dispose();
		}
		_menuDelegate?.Dispose();
		_menuDelegate = null;
		_menuHost = null;
	}
	private DocumentDelegate? _documentDelegate;
	private WebKit.WKJavascriptEvaluationResult? _framesCompleted;

	partial void ObservePlatform()
	{
		if (Handler?.PlatformView is WebKit.WKWebView web)
		{
			_documentDelegate ??= new DocumentDelegate(this);
			web.NavigationDelegate = _documentDelegate;
			EnsureMenuFor(web);
		}
	}

	private partial void SendFrames(ReadOnlyMemory<byte> script)
	{
		if (Handler?.PlatformView is not WebKit.WKWebView web) { DocumentFailed("The frame sender has no native handler."); return; }
		_framesCompleted ??= (_, error) => { if (error is not null) { DocumentFailed(error.LocalizedDescription); } };
		try
		{
			if (!MemoryMarshal.TryGetArray(script, out ArraySegment<byte> bytes)) { throw new InvalidOperationException("The frame script has no reusable UTF-8 storage."); }
			// NSString decodes synchronously; NSData only borrows the pinned UTF-8 writer storage.
			Foundation.NSString source;
			GCHandle pin = GCHandle.Alloc(bytes.Array, GCHandleType.Pinned);
			try
			{
				using Foundation.NSData data = Foundation.NSData.FromBytesNoCopy(pin.AddrOfPinnedObject() + bytes.Offset, (nuint)bytes.Count, false);
				source = new Foundation.NSString(data, Foundation.NSStringEncoding.UTF8);
			}
			finally { pin.Free(); }
			using (source) { web.EvaluateJavaScript(source, _framesCompleted); }
		}
		catch (Exception ex) { DocumentFailed(ex.Message); }
	}

	private sealed class DocumentDelegate(TerminalView owner) : WebKit.WKNavigationDelegate
	{
		public override void ContentProcessDidTerminate(WebKit.WKWebView webView) => owner.DocumentFailed("WebKit content process terminated.");
	}

	partial void PresentMenu()
	{
		if (Handler?.PlatformView is not UIView view || _menu is null) { return; }
		EnsureMenuFor(view);
		if (_menu is null) { return; }
		CGRect rect = MenuRect();
		_menuConfiguration = UIEditMenuConfiguration.Create(null, new CGPoint(rect.GetMidX(), rect.Top));
		_menuInteraction!.PresentEditMenu(_menuConfiguration);
	}

	partial void DismissMenu()
	{
		_pasteContext = null;
		_menuConfiguration = null;
		_menuInteraction?.DismissMenu();
	}

	partial void ReleasePlatform(object? native)
	{
		MenuHandlerChanging(native);
		if (native is WebKit.WKWebView web) { web.StopLoading(); web.WeakNavigationDelegate = null; }
		_documentDelegate?.Dispose();
		_documentDelegate = null;
		_framesCompleted = null;
	}

	private CGRect MenuRect() => _menu is null ? CGRect.Empty : new CGRect(_menu.X, _menu.Y, _menu.Width, _menu.Height);

	private sealed class MenuDelegate(TerminalView view, int generation) : UIEditMenuInteractionDelegate
	{
		public override UIMenu? GetMenu(UIEditMenuInteraction interaction, UIEditMenuConfiguration configuration, UIMenuElement[] suggestedActions)
		{
			if (generation != view._menuGeneration || interaction != view._menuInteraction || configuration != view._menuConfiguration
				|| view._menu is not { } context || !view._leaseActive) { return null; }
			string origin = view._lease;
			IEnumerable<UIMenuElement> Walk(IEnumerable<UIMenuElement> entries)
			{
				foreach (UIMenuElement entry in entries)
				{
					yield return entry;
					if (entry is UIMenu group) { foreach (UIMenuElement child in Walk(group.Children)) { yield return child; } }
				}
			}
			UICommand? paste = Walk(suggestedActions).OfType<UICommand>().FirstOrDefault(command => command.Action.Name == "paste:");
			view._pasteContext = paste is null ? null : (origin, context.Id, generation);
			List<UIMenuElement> result = [];
			foreach (var item in view.MenuItems())
			{
				if (item.Action == "paste") { if (paste is not null) { result.Add(paste); } continue; }
				result.Add(UIAction.Create(item.Title, null, item.Action, _ =>
				{
					if (generation == view._menuGeneration && interaction == view._menuInteraction && configuration == view._menuConfiguration) { view.ActOnMenu(item.Action, origin, context.Id); }
				}));
			}
			return UIMenu.Create([.. result]);
		}

		public override CGRect GetTargetRect(UIEditMenuInteraction interaction, UIEditMenuConfiguration configuration) =>
			generation == view._menuGeneration && interaction == view._menuInteraction && configuration == view._menuConfiguration ? view.MenuRect() : CGRect.Empty;

		public override void WillDismissMenu(UIEditMenuInteraction interaction, UIEditMenuConfiguration configuration, IUIEditMenuInteractionAnimating animator)
		{
			animator.AddCompletion(() =>
			{
				if (generation != view._menuGeneration || interaction != view._menuInteraction || configuration != view._menuConfiguration) { return; }
				// UIKit may dismiss before dispatching paste:; the selection/lease owns that operation.
				view._menuConfiguration = null;
			});
		}
	}
}

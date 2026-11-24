using Android.Views;

namespace HerdrRemote.Pages;

// The selection's edit menu on Android: the system's own floating action mode (ActionModeType.Floating) on the web view, at the
// selection's rectangle, with the page's actions as menu items. The page's CSS pixels times the display density are the web view's
// pixels (the page is not zoomed or scrolled).
public sealed partial class TerminalView
{
	private ActionMode? _actionMode;
	private int _actionMenuId;
	private DocumentClient? _documentClient;

	partial void ObservePlatform()
	{
		if (Handler is Microsoft.Maui.Handlers.HybridWebViewHandler handler && handler.PlatformView is { } web)
		{
			_documentClient ??= new DocumentClient(handler, this);
			web.SetWebViewClient(_documentClient);
		}
	}

	private sealed class DocumentClient(Microsoft.Maui.Handlers.HybridWebViewHandler handler, TerminalView owner)
		: Microsoft.Maui.Platform.MauiHybridWebViewClient(handler)
	{
		public override bool OnRenderProcessGone(Android.Webkit.WebView? view, Android.Webkit.RenderProcessGoneDetail? detail)
		{
			owner.DocumentFailed("Chromium renderer process terminated.");
			return true;
		}
	}

	partial void PresentMenu()
	{
		if (Handler?.PlatformView is not Android.Webkit.WebView web || _menu is null) { return; }
		if (_actionMode is not null && _actionMenuId != _menu.Id) { DismissMenu(); }
		if (_actionMode is null)
		{
			_actionMenuId = _menu.Id;
			_actionMode = web.StartActionMode(new MenuCallback(this), ActionModeType.Floating);
			return;
		}
		_actionMode.Invalidate();
		_actionMode.InvalidateContentRect();
	}

	partial void DismissMenu()
	{
		ActionMode? mode = _actionMode;
		_actionMode = null;
		mode?.Finish();
	}

	partial void ReleasePlatform(object? native)
	{
		ActionMode? mode = _actionMode;
		_actionMode = null;
		try { mode?.Finish(); }
		finally { mode?.Dispose(); }
		if (native is Android.Webkit.WebView web) { web.StopLoading(); web.Destroy(); }
		_documentClient?.Dispose();
		_documentClient = null;
	}

	private sealed class MenuCallback(TerminalView view) : ActionMode.Callback2
	{
		private readonly string _origin = view._lease;
		private readonly int _id = view._menu!.Id;
		// The items of the menu now, by item id.
		private List<(string Action, string Title)> _items = [];

		public override bool OnCreateActionMode(ActionMode? mode, IMenu? menu) => true;

		public override bool OnPrepareActionMode(ActionMode? mode, IMenu? menu)
		{
			if (menu is null || !view._leaseActive || _origin != view._lease || view._menu?.Id != _id) { return false; }
			menu.Clear();
			_items = view.MenuItems();
			for (int i = 0; i < _items.Count; i++) { menu.Add(0, i, i, _items[i].Title)?.SetShowAsAction(ShowAsAction.Always); }
			return true;
		}

		public override bool OnActionItemClicked(ActionMode? mode, IMenuItem? item)
		{
			if (mode != view._actionMode || item is null || item.ItemId < 0 || item.ItemId >= _items.Count) { return false; }
			view.ActOnMenu(_items[item.ItemId].Action, _origin, _id);
			return true;
		}

		// The system ended it (a touch elsewhere, the page gone): the next menu starts a new one.
		public override void OnDestroyActionMode(ActionMode? mode)
		{
			if (view._actionMode == mode) { view._actionMode = null; }
		}

		public override void OnGetContentRect(ActionMode? mode, Android.Views.View? owner, Android.Graphics.Rect? outRect)
		{
			if (outRect is null || view._menu is not { } menu || owner is null) { return; }
			float density = owner.Resources?.DisplayMetrics?.Density ?? 1;
			outRect.Set((int)(menu.X * density), (int)(menu.Y * density), (int)((menu.X + menu.Width) * density), (int)((menu.Y + menu.Height) * density));
		}
	}
}

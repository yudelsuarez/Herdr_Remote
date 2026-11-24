using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;
using NativeToolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace HerdrRemote;

// AppShell's native chrome is permanently hidden. A native-toolbar feature must replace this policy with an owned tracker.
internal sealed class AppShellRenderer : ShellRenderer
{
	protected override IShellToolbarTracker CreateTrackerForToolbar(NativeToolbar toolbar) => new HiddenToolbarTracker(toolbar);

	// Do not construct or inherit the stock tracker: its constructor installs an unused AppBar global-layout listener.
	private sealed class HiddenToolbarTracker(NativeToolbar platformToolbar) : IShellToolbarTracker
	{
		private NativeToolbar? _platformToolbar = platformToolbar;
		private IToolbar? _toolbar;
		private bool _disposed;

		public Page? Page { get; set; }
		public bool CanNavigateBack { get; set; }
		public Color? TintColor { get; set; }

		public void SetToolbar(IToolbar toolbar)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			ArgumentNullException.ThrowIfNull(toolbar);
			_toolbar = toolbar;
			if (toolbar is Microsoft.Maui.Controls.Toolbar controlsToolbar) { controlsToolbar.IsVisible = false; }
		}

		public void Dispose()
		{
			if (_disposed) { return; }
			_disposed = true;
			IToolbar? toolbar = _toolbar;
			NativeToolbar? native = _platformToolbar;
			_toolbar = null;
			_platformToolbar = null;
			Page = null;
			TintColor = null;
			try { toolbar?.Handler?.DisconnectHandler(); }
			finally { native?.RemoveAllViews(); } // the fragment owns the toolbar itself, as in MAUI's stock cleanup
		}
	}
}

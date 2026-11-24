using Microsoft.Extensions.Logging;
using HerdrRemote.Pages;

namespace HerdrRemote;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
#if IOS
		// Keys stay on this iPhone: the SSH key and API keys are excluded from iCloud/device backups and device-to-device transfers.
		SecureStorage.DefaultAccessible = Security.SecAccessible.AfterFirstUnlockThisDeviceOnly;
#endif
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("icons.ttf", "icons");
				fonts.AddFont("Geist-Regular.ttf", "Geist");
				fonts.AddFont("Geist-Medium.ttf", "GeistMedium");
				fonts.AddFont("Geist-SemiBold.ttf", "GeistSemiBold");
				fonts.AddFont("GeistMono-Regular.ttf", "GeistMono");
				fonts.AddFont("GeistMono-Medium.ttf", "GeistMonoMedium");
			});

		#region Native Control Visual Adjustments
		builder.ConfigureMauiHandlers(handlers =>
		{
			handlers.AddHandler<TerminalHost, TerminalHostHandler>();
			handlers.AddHandler<TerminalKeys, TerminalKeysHandler>();
#if IOS
			Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("UnifiedEntry", (handler, view) =>
			{
				var tf = handler.PlatformView;

				tf.BorderStyle = UIKit.UITextBorderStyle.None;

				// Keep the clear button behavior consistent across platforms.
				tf.ClearButtonMode = UIKit.UITextFieldViewMode.Never;

				// Empty side views remove the native horizontal insets.
				tf.LeftView = new UIKit.UIView(new CoreGraphics.CGRect(0, 0, 0, 0));
				tf.LeftViewMode = UIKit.UITextFieldViewMode.Always;
				tf.RightView = new UIKit.UIView(new CoreGraphics.CGRect(0, 0, 0, 0));
				tf.RightViewMode = UIKit.UITextFieldViewMode.Always;

				tf.BackgroundColor = UIKit.UIColor.Clear;
				if (view is Entry entry && (entry.AutomationId == "TerminalInput" || entry.AutomationId == "TerminalFind") && tf.InputAccessoryView is not null)
				{
					tf.InputAccessoryView = null;
					if (tf.IsFirstResponder) { tf.ReloadInputViews(); }
				}
			});
			// Remove MAUI's floating Done button below the keys. The keyboard key dismisses the keyboard for every pane input.
			Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoDoneBar", (handler, view) =>
			{
				if (view is Editor { AutomationId: "DictationDraft" } && handler.PlatformView.InputAccessoryView is not null)
				{
					handler.PlatformView.InputAccessoryView = null;
					if (handler.PlatformView.IsFirstResponder) { handler.PlatformView.ReloadInputViews(); }
				}
			});
			// UIKit's default ScaleToFill stretches an existing drawing instead of redrawing it.
			// A CollectionView row can start at zero width, so redraw its shapes to make dividers appear when the row grows.
			Microsoft.Maui.Handlers.ShapeViewHandler.Mapper.AppendToMapping("RedrawOnResize", (handler, view) =>
				handler.PlatformView.ContentMode = UIKit.UIViewContentMode.Redraw);
#elif ANDROID
			handlers.AddHandler<AppShell, AppShellRenderer>();
			// End a dragged press on Android as on iOS.
			handlers.AddHandler<HoldTap.HoldButton, HoldButtonHandler>();
			Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("UnifiedEntry", (handler, view) =>
			{
				var et = handler.PlatformView;

				et.Background = null;
				et.BackgroundTintList = null;

				// Use single-line font metrics to keep the baseline stable.
				et.SetSingleLine(true);
				// SetSingleLine replaces the transformation method; restore password masking if MAUI already mapped it.
				if (view.IsPassword)
					et.TransformationMethod = Android.Text.Method.PasswordTransformationMethod.Instance;

				et.SetIncludeFontPadding(false);

				et.Gravity = Android.Views.GravityFlags.CenterVertical | Android.Views.GravityFlags.Start;

				// Zero padding can make the Android baseline appear high.
				et.SetPadding(0, 0, 0, 0);
				et.SetPaddingRelative(0, 0, 0, 0);

				et.SetMinHeight(0);
				et.SetMinimumHeight(0);

				UnfocusWhenKeyboardGoes(et, view);
			});
			// Use the same flat background for multiline inputs.
			Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("UnifiedEditor", (handler, view) =>
			{
				handler.PlatformView.Background = null;
				handler.PlatformView.BackgroundTintList = null;
				UnfocusWhenKeyboardGoes(handler.PlatformView, view);
			});
			static void UnfocusWhenKeyboardGoes(Android.Widget.EditText et, IView view)
			{
				// Back can hide the keyboard without clearing focus, leaving the tab bar hidden.
				// Clear focus whenever the keyboard closes so the page can restore the bar.
				bool imeShown = false;
				void OnLayout(object? s, EventArgs e)
				{
					bool shown = AndroidX.Core.View.ViewCompat.GetRootWindowInsets(et)?.IsVisible(AndroidX.Core.View.WindowInsetsCompat.Type.Ime()) == true;
					if (imeShown && !shown && et.HasFocus) { view.Unfocus(); }
					imeShown = shown;
				}
				et.ViewAttachedToWindow += (_, _) => { imeShown = false; et.ViewTreeObserver?.GlobalLayout += OnLayout; };
				et.ViewDetachedFromWindow += (_, _) => { if (et.ViewTreeObserver is { IsAlive: true } o) { o.GlobalLayout -= OnLayout; } };
			}
#endif
		});
		#endregion Native Control Visual Adjustments

#if DEBUG
		builder.Logging.AddDebug();
		builder.Services.AddHybridWebViewDeveloperTools();
#endif

		return builder.Build();
	}
}

using Languages;
using Popups;

namespace HerdrRemote.Pages;

// Shares double-tap protection, button disabling, error handling and cleanup across a page's handlers.
// Use one instance per page; its busy box is hidden on exit if the page is still visible.
internal sealed class BusyRunner(IsBusyBox busyBox)
{
	bool _busy;

	// Catches failures before they reach async void handlers, and always resets the busy flag and button.
	// Navigation handlers can set showError to false to log only; onError restores state disabled by the body.
	public async Task RunAsync(string tag, Func<Task> body, Button? button = null, bool showError = true, Action? onError = null)
	{
		if (_busy) { return; }
		try
		{
			_busy = true;
			if (button is not null) { button.IsEnabled = false; }
			await body();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"[{tag}] unexpected: {ex}");
			onError?.Invoke();
			if (showError) { await Show("Error", i18n.Translate("Something went wrong, please try again")); }
		}
		finally
		{
			// Keep a departing page covered until the next page is drawn.
			if (OnScreen()) busyBox.IsVisible = false;
			if (button is not null) { button.IsEnabled = true; }
			_busy = false;
		}
	}

	// Checks whether the busy box belongs to the visible page in its own window's Shell.
	bool OnScreen()
	{
		Element? element = busyBox;
		while (element is not null and not Page) element = element.Parent;
		return element is Page page && (page.Window?.Page as Shell)?.CurrentPage == page;
	}

	// Shows an OK popup with a catalog-key title and an already translated message; an empty message shows nothing.
	public static Task Show(string title, string message, Func<ContentPage, Layout, bool>? CanInsert = null)
		=> message.Length == 0 ? Task.CompletedTask : PopupsHandle.PopDisplayAsync(i18n.Translate(title), message, [i18n.Translate("OK")], CanInsert: CanInsert);
}

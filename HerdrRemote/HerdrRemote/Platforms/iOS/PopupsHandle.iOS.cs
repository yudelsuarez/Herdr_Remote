#if IOS
namespace Popups;

public static partial class PopupsHandle
{
	// Asks the current first responder, wherever it is, to resign: closes the keyboard even when no tracked field (Entry or Editor) is focused.
	private static partial void HideWindowKeyboard()
		=> UIKit.UIApplication.SharedApplication.SendAction(new ObjCRuntime.Selector("resignFirstResponder"), null, null, null);
}
#endif

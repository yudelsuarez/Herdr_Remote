#if ANDROID
namespace Popups;

public static partial class PopupsHandle
{
	private static partial void HideWindowKeyboard()
	{
		if (Platform.CurrentActivity?.Window?.DecorView?.WindowToken is not { } token) return;
		if (Platform.CurrentActivity.GetSystemService(Android.Content.Context.InputMethodService) is Android.Views.InputMethods.InputMethodManager imm)
			imm.HideSoftInputFromWindow(token, Android.Views.InputMethods.HideSoftInputFlags.None);
	}
}
#endif

using Microsoft.Maui.Handlers;
using HerdrRemote.Remote;
#if IOS
using NativeKeys = HerdrRemote.Pages.IosTerminalKeys;
#else
using NativeKeys = HerdrRemote.Pages.AndroidTerminalKeys;
#endif

namespace HerdrRemote.Pages;

public sealed class TerminalKeys : View
{
	internal event Action<string>? TextReceived;
	internal event Action<string>? KeyReceived;
	internal event Action? PasteRequested;
#if IOS
	internal event Action<bool>? ActiveChanged;
	internal void Active(bool value) => ActiveChanged?.Invoke(value);
#endif
	internal void Text(string value) => TextReceived?.Invoke(value);
	internal void Key(string value) => KeyReceived?.Invoke(value);
	internal void Paste() => PasteRequested?.Invoke();
	internal void Open() => (Handler?.PlatformView as NativeKeys)?.Open();
	internal void Close() => (Handler?.PlatformView as NativeKeys)?.Close();

	internal void HardwareKey(string key, bool control, bool alt, bool shift) =>
		Key(TerminalKeyEvents.Hardware(key, control, alt, shift));

#if ANDROID
	internal event Action? CorrectionBackspaceRequested;
	internal void CorrectionBackspace() => CorrectionBackspaceRequested?.Invoke();
#endif
	internal void ResetComposition()
	{
#if ANDROID
		(Handler?.PlatformView as NativeKeys)?.ResetComposition();
#endif
	}
}

internal sealed class TerminalKeysHandler() : ViewHandler<TerminalKeys, NativeKeys>(ViewMapper)
{
	protected override NativeKeys CreatePlatformView()
#if IOS
		=> new(VirtualView);
#else
		=> new(Context, VirtualView);
#endif
}

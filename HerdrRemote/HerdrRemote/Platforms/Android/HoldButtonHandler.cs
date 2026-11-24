using Android.Content;
using Android.Views;
using Google.Android.Material.Button;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace HerdrRemote.Pages;

// Watch drags in native dispatch to preserve MAUI's touch listener: Android allows only one per view.
// ButtonHandler raises Pressed on DOWN and Released on UP/CANCEL. Recheck CreatePlatformView and
// ConnectHandler when upgrading from MAUI 10.0.120.
internal sealed class HoldButtonHandler : ButtonHandler
{
	protected override MaterialButton CreatePlatformView()
	{
		// The same four settings as MAUI's own CreatePlatformView (ButtonHandler, MAUI 10.0.120); its transparent list is private.
		DragWatchButton button = new(Context)
		{
			IconGravity = MaterialButton.IconGravityTextStart,
			IconTintMode = Android.Graphics.PorterDuff.Mode.Add,
			IconTint = Colors.Transparent.ToDefaultColorStateList(),
			SoundEffectsEnabled = false,
		};
		return button;
	}
}

// Cancel the hold once movement exceeds 10 dp on either axis; still forward every event to MAUI.
// Retain only HoldTap.Press flags, never the row.
internal sealed class DragWatchButton(Context context) : MauiMaterialButton(context)
{
	private const float Slop = 10;
	private float _startX;
	private float _startY;

	public HoldTap.Press? Press { get; set; }

	public override bool DispatchTouchEvent(MotionEvent? e)
	{
		if (e is not null && Press is { } press)
		{
			if (e.ActionMasked == MotionEventActions.Down)
			{
				(_startX, _startY) = (e.RawX, e.RawY);
				press.Begin();
			}
			else if (e.ActionMasked == MotionEventActions.Up) { press.End(); }
			else if (e.ActionMasked is MotionEventActions.Cancel or MotionEventActions.PointerDown) { press.Cancel(); }
			else if (e.ActionMasked == MotionEventActions.Move && !press.Dragged)
			{
				float slop = Slop * (Resources?.DisplayMetrics?.Density ?? 1);
				if (Math.Abs(e.RawX - _startX) > slop || Math.Abs(e.RawY - _startY) > slop)
				{
					press.Move();
				}
			}
		}
		return base.DispatchTouchEvent(e);
	}
}

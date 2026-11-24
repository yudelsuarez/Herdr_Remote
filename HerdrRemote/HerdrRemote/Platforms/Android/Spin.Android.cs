using Android.Views.Animations;

namespace HerdrRemote.Pages;

// Rotate 0–360 degrees every 900 ms, linearly, around the view's center; stopping restores it upright.
// Relative RotateAnimation pivots use the size at draw time. ObjectAnimator uses MAUI's mapped
// AnchorX/AnchorY-based pivots, which are zero before the first row layout and rotate the ring around its corner.
internal sealed partial class Spinner
{
	private RotateAnimation? _animation;
	private Android.Views.View? _turning;

	partial void PlatformStart(object platformView)
	{
		if (platformView is not Android.Views.View view) { return; }
		if (ReferenceEquals(_turning, view) && _animation is { HasStarted: true, HasEnded: false }) { return; }
		PlatformStop();
		_animation = new RotateAnimation(0f, 360f, Dimension.RelativeToSelf, 0.5f, Dimension.RelativeToSelf, 0.5f)
		{
			Duration = 900,
			Interpolator = new LinearInterpolator(),
			RepeatCount = Android.Views.Animations.Animation.Infinite,
		};
		view.StartAnimation(_animation);
		_turning = view;
	}

	partial void PlatformStop()
	{
		_turning?.ClearAnimation();
		_animation?.Dispose();
		_animation = null;
		_turning = null;
	}
}

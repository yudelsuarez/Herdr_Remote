using CoreAnimation;
using Foundation;
using UIKit;

namespace HerdrRemote.Pages;

// The iOS turn: a CABasicAnimation on the layer's transform.rotation.z under the key "spin", removed on stop. RemovedOnCompletion =
// false keeps it across a trip to the background (UIKit removes completed layer animations otherwise).
internal sealed partial class Spinner
{
	private const string Key = "spin";

	private UIView? _turning;

	partial void PlatformStart(object platformView)
	{
		if (platformView is not UIView view) { return; }
		if (ReferenceEquals(_turning, view) && view.Layer.AnimationForKey(Key) is not null) { return; }
		PlatformStop();
		CABasicAnimation turn = CABasicAnimation.FromKeyPath("transform.rotation.z");
		turn.From = NSNumber.FromDouble(0);
		turn.To = NSNumber.FromDouble(2 * Math.PI);
		turn.Duration = 0.9;
		turn.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear);
		turn.RepeatCount = float.PositiveInfinity;
		turn.RemovedOnCompletion = false;
		view.Layer.AddAnimation(turn, Key);
		_turning = view;
	}

	partial void PlatformStop()
	{
		_turning?.Layer.RemoveAnimation(Key);
		_turning = null;
	}
}

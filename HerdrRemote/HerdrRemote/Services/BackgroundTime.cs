namespace HerdrRemote.Services;

// Requests OS background time so the SSH session survives brief switches away from the app.
// Platform implementations are in Platforms/<os>/BackgroundTime.<os>.cs.
public static partial class BackgroundTime
{
	// Requests background time; expired runs on the UI thread just before it expires and must close resources immediately.
	// Calling Begin again ends the previous request.
	public static partial void Begin(Action expired);

	// Releases background time when the app returns to the foreground or closes its session.
	public static partial void End();
}

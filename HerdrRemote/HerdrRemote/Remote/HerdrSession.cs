using System.Diagnostics;
using System.Net.Sockets;
using HerdrRemote.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HerdrRemote.Remote;

// Connection states shown by the header icon and state card.
internal enum SessionState
{
	Disconnected,
	NotConfigured,
	Connecting,
	Connected,
	HostKeyUnknown,
	HostKeyChanged,
	AuthFailed,
	// The Mac refused the password, or does not allow password sign-in.
	PasswordRefused,
	Unreachable,
	// No hc (herdr-core) on the Mac.
	HerdrNotFound,
	// hc is installed, but herdr-core did not respond or its connection could not be restored.
	HerdrNotRunning,
	Failed,
	// The retry loop or core-channel recovery is restoring the connection.
	Reconnecting,
	// The user tapped Disconnect. This state lasts until the next connection and is not persisted.
	DisconnectedByOwner,
}

// Owns the app's single SSH session (App.Herdr) and one `hc connect` exec channel.
// Agent events and the open terminal share that core connection, so disconnecting closes both.
// If the core channel drops while SSH remains connected, reopen it and reattach the terminal; programs keep running in the core.
// An SSH error, failed core-channel recovery or watchdog timeout starts a full reconnect using RetryDelays.
// SSH.NET keepalive stays disabled. Core event heartbeats arrive every 15 s; WatchdogSilence without a frame counts as a drop.
// Password sign-in is also used once to add the phone's public key to the Mac's authorized_keys.
internal sealed class HerdrSession
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];
	private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromSeconds(15);
	private static readonly TimeSpan WatchdogSilence = TimeSpan.FromSeconds(45);
	// Up to 15 minutes on iOS and Android, subject to earlier OS expiry; suspension may defer cleanup until resume.
	private static readonly TimeSpan BackgroundLimit = TimeSpan.FromMinutes(15);
	// Deadline for the round trip on resume.
	private static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(2);

	private SshClient? _client;
	private CoreConnection? _core;
	// Connection-scoped recovery and watchdog work; Teardown replaces this token source.
	private CancellationTokenSource _lifetime = new();
	// Try again, Window.Resumed and notification taps share a single in-flight connection attempt.
	private Task? _connecting;
	// The current ConnectAsync or RestartAsync attempt. Disconnect cancels it at its next await.
	private CancellationTokenSource? _attempt;
	// Retry-loop cancellation is separate from _lifetime, which Teardown renews on each attempt.
	// Only Disconnect or an explicit connection request cancels the retry loop.
	private CancellationTokenSource? _retry;
	// While retrying, SetState gates the individual attempts' state changes.
	private bool _recovering;
	// The retry attempt's first result. SetState may publish it after the attempt returns.
	private TaskCompletionSource<SessionState>? _attemptResult;
	// The last transient failure, published if all retry attempts fail.
	private (SessionState State, string? Detail) _lastResult;
	// Request notification permission only on the first connection of each app run.
	private bool _notificationsAsked;
	// Background cancellation, entry time and closure state. Returning to the foreground cancels the background task.
	private CancellationTokenSource? _background;
	private long _backgroundSince;
	private bool _closedInBackground;

	public SessionState State { get; private set; }

	// Raised on the UI thread after every state change.
	public event Action? StateChanged;

	// The offered host key when it does not match the pin ("{type} SHA256:{fingerprint}"). The page requests user trust.
	public string? OfferedHostKey { get; private set; }

	// The message for the current failure state: the core's error, hc's stderr or the exception message.
	public string? Detail { get; private set; }

	// Core API access; null while disconnected from the core.
	public CoreApi? Api { get; private set; }

	// The terminals and their agents, kept current by the core's events.
	public HerdrAgents Agents { get; } = new();

	// Settings-based machine identity for retained agent statuses and notifications across reconnects.
	internal static string MachineKey => $"{RemoteSettings.User}@{RemoteSettings.Host}:{RemoteSettings.Port}";

	// The open terminal's stream, set by the Pane screen while a terminal is open; a disconnect closes it and a connect reattaches it.
	public TerminalStream? Terminal { get; set; }

	// An explicit connection request (initial connection, Try again, resume, notification tap or Trust) ends the retry loop.
	public Task ConnectAsync(CancellationToken ct, byte[]? password = null)
	{
		if (_connecting is { IsCompleted: false }) { return _connecting; }
		_retry?.Cancel();
		_recovering = false;
		_attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
		return _connecting = ConnectOnceAsync(password, _attempt.Token);
	}

	// Cancel the retry loop and current attempt, wait for it to end, then connect with the saved settings.
	// The old client cannot publish state after replacement. Disconnect throws OperationCanceledException.
	// Create the new token before waiting so Disconnect during the wait prevents the new attempt from starting.
	public async Task RestartAsync(byte[]? password)
	{
		_retry?.Cancel();
		_recovering = false;
		_attempt?.Cancel();
		CancellationTokenSource attempt = _attempt = new CancellationTokenSource();
		if (_connecting is { } old) { try { await old; } catch (Exception) { } }
		attempt.Token.ThrowIfCancellationRequested();
		if (_attempt != attempt) { throw new OperationCanceledException(); } // A newer connection request takes precedence.
		_connecting = ConnectOnceAsync(password, attempt.Token);
		await _connecting;
	}

	private async Task ConnectOnceAsync(byte[]? password, CancellationToken ct)
	{
		if (_client is not null) { Teardown(); }
		if (!RemoteSettings.IsConfigured)
		{
			SetState(SessionState.NotConfigured, null);
			return;
		}
		OfferedHostKey = null;
		SetState(SessionState.Connecting, null);
		SshClient? client = null;
		try
		{
			ConnectionInfo info = new(RemoteSettings.Host, RemoteSettings.Port, RemoteSettings.User,
				password is null
					? new PrivateKeyAuthenticationMethod(RemoteSettings.User, await SshKeyStore.PrivateKeyAsync())
					: new PasswordAuthenticationMethod(RemoteSettings.User, password)) { Timeout = ConnectTimeout };
			client = _client = new SshClient(info);
			client.HostKeyReceived += OnHostKeyReceived;
			client.ErrorOccurred += OnErrorOccurred;
			await client.ConnectAsync(ct);
			if (password is not null)
			{
				// After password sign-in, append the phone's public key without duplicates, separating any unterminated last line.
				// Then reconnect using key authentication.
				string line = await SshKeyStore.PublicLineAsync();
				using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
				deadline.CancelAfter(RequestTimeout);
				using SshCommand add = client.CreateCommand($"""set -e; umask 077; d="$HOME/.ssh"; f="$d/authorized_keys"; mkdir -p "$d"; chmod 700 "$d"; touch "$f"; chmod 600 "$f"; if ! grep -qF -- {Quote(line.Split(' ')[1])} "$f"; then if [ -s "$f" ] && [ -n "$(tail -c 1 "$f")" ]; then printf '\n' >> "$f"; fi; printf '%s\n' {Quote(line)} >> "$f"; fi""");
				await add.ExecuteAsync(deadline.Token);
				if (_client != client) { return; }
				if (add.ExitStatus == 0)
				{
					await ConnectOnceAsync(null, ct);
					return;
				}
				SetState(SessionState.Failed, add.Error);
				return;
			}
			await OpenCoreAsync(client, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Close only this attempt's client. A newer client stays active; the cancelling caller publishes its own state.
			if (client is not null && _client == client) { Teardown(); }
			throw;
		}
		catch (Exception ex)
		{
			// Ignore failures from clients replaced or torn down while connecting, including background teardown.
			if (_client == client) { Fail(ex, client, password is not null); }
		}
	}

	// Open `hc connect`, then subscribe to agents and reattach the terminal.
	// Reopen a dropped core channel while its SSH client is still current; programs survive a core restart or update.
	private async Task OpenCoreAsync(SshClient client, CancellationToken ct)
	{
		CoreConnection core;
		try
		{
			core = await CoreConnection.OpenAsync(client, ct);
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			// Exit 127 means hc is missing; a nonresponsive core has a separate state. Try again starts a new connection.
			if (_client == client) { SetState(ex is CoreConnectException { NotInstalled: true } ? SessionState.HerdrNotFound : SessionState.HerdrNotRunning, ex.Message); }
			return;
		}
		if (_client != client)
		{
			await core.DisposeAsync();
			return;
		}
		_core = core;
		CoreApi api = new(core);
		Api = api;
		string machine = core.Hello["core"]?["name"]?.GetValue<string>() ?? RemoteSettings.Host;
		// Watch channel completion independently: connecting returns before the channel ends.
		// Dispose the dropped exec channel before reopening. Failed recovery must not leave the session Connected without a core.
		CancellationToken lifetime = _lifetime.Token;
		_ = Task.Run(async () =>
		{
			await core.Lost;
			await MainThread.InvokeOnMainThreadAsync(async () =>
			{
				// Ignore intentional closure or an SSH drop already handled by ErrorOccurred.
				if (_core != core || _client != client || !client.IsConnected) { return; }
				Debug.WriteLine("[Herdr] core line lost; opening it again");
				Terminal?.Close();
				Agents.Stop();
				Api = null;
				_core = null;
				await core.DisposeAsync();
				SetState(SessionState.Reconnecting, null);
				try
				{
					await OpenCoreAsync(client, lifetime);
				}
				catch (Exception ex)
				{
					// Preserve Disconnect's state on cancellation; otherwise show the failure so Try again can reconnect.
					Debug.WriteLine($"[Herdr] core reopen failed: {ex.GetType().Name}: {ex.Message}");
					if (!lifetime.IsCancellationRequested && _client == client) { SetState(SessionState.HerdrNotRunning, ex.Message); }
				}
			});
		}, CancellationToken.None);
		// Agent data, terminal state and session state are updated on the UI thread.
		await MainThread.InvokeOnMainThreadAsync(async () =>
		{
			try
			{
				await Agents.StartAsync(api, machine, MachineKey);
			}
			catch (Exception ex)
			{
				if (_core == core) { SetState(SessionState.HerdrNotRunning, ex.Message); }
				return;
			}
			if (_core != core) { return; }
			SetState(SessionState.Connected, null);
			_ = WatchAsync(core, lifetime);
			// Do not await: iOS completes this only after the user responds, and connection setup must not wait.
			if (!_notificationsAsked)
			{
				_notificationsAsked = true;
				_ = Services.AgentNotifier.RequestPermissionAsync();
			}
			Terminal?.Reopen();
		});
	}

	// Upload an image, photo or file over a separate SSH exec channel and return its path for the agent.
	// Each upload uses $TMPDIR/remoteshell/<yyyyMMdd-HHmmss>-<6 hex>/<name> to preserve its filename without collisions.
	// Names retain letters, digits, ".", "_" and "-"; other runs become "-", so the returned path needs no shell quoting.
	// Before creating the directory, remove files older than a day, then empty directories older than a day.
	// This avoids deleting a newly created upload directory. Agents are expected to read uploads soon after receiving them.
	// RequestTimeout measures lack of progress and resets after each block, so large uploads are not cut off.
	public async Task<string> UploadAsync(Stream data, string name, CancellationToken ct)
	{
		if (_client is not { IsConnected: true } client) { throw new InvalidOperationException("The SSH session is not connected."); }
		string folder = $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(RequestTimeout);
		using SshCommand command = client.CreateCommand($$"""d="${TMPDIR:-/tmp}"; d="${d%/}/remoteshell"; mkdir -p "$d" && find "$d" -type f -mmin +1440 -delete; find "$d" -mindepth 1 -type d -empty -mmin +1440 -delete; u="$d/{{folder}}"; mkdir "$u" && f="$u/{{SafeFileName(name)}}" && cat > "$f" && printf '%s' "$f" """);
		Task run = command.ExecuteAsync(deadline.Token);
		// Closing stdin ends cat's input, and the command answers with the path.
		await using (Stream input = command.CreateInputStream())
		{
			byte[] block = new byte[81920];
			int read;
			while ((read = await data.ReadAsync(block, deadline.Token)) > 0)
			{
				await input.WriteAsync(block.AsMemory(0, read), deadline.Token);
				deadline.CancelAfter(RequestTimeout);
			}
		}
		await run;
		if (command.ExitStatus != 0) { throw new IOException(command.Error.Trim()); }
		return command.Result.Trim();
	}

	// Make a filename safe for unquoted use by the Mac's shell and agents. After NFC normalization, retain letters, digits,
	// ".", "_" and "-"; replace other runs with one "-". Empty names and dot-only names (including "." and "..") become "file".
	internal static string SafeFileName(string name)
	{
		System.Text.StringBuilder safe = new();
		foreach (char c in name.Normalize(System.Text.NormalizationForm.FormC))
		{
			if (char.IsLetterOrDigit(c) || c is '.' or '_' or '-') { safe.Append(c); }
			else if (safe.Length == 0 || safe[^1] != '-') { safe.Append('-'); }
		}
		string result = safe.ToString();
		return result.Trim('.').Length == 0 ? "file" : result;
	}

	// On Window.Stopped, keep the connection while the OS allows background work, up to BackgroundLimit.
	// Then close it; LeaveBackground reconnects on return.
	public void EnterBackground()
	{
		_background?.Cancel();
		_background = new CancellationTokenSource();
		CancellationToken away = _background.Token;
		_backgroundSince = Environment.TickCount64;
#if ANDROID
		if (State == SessionState.Connected)
#endif
		{
			BackgroundTime.Begin(() => CloseForBackground(away));
		}
		_ = Task.Delay(BackgroundLimit, away).ContinueWith(_ => MainThread.BeginInvokeOnMainThread(() => CloseForBackground(away)),
			CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
	}

	// On every return, check a retained connection without repainting. Closed or expired sessions reconnect.
	public void LeaveBackground()
	{
		long back = Environment.TickCount64;
		bool away = _background is not null;
		_background?.Cancel();
		_background = null;
		BackgroundTime.End();
		bool closed = _closedInBackground;
		_closedInBackground = false;
		// Cancel a pending retry delay now that the app has returned to the network.
		if (!RemoteSettings.IsConfigured || State is SessionState.DisconnectedByOwner or SessionState.Connecting) { return; }
		// Check the deadline again: Android has no expiration callback and a suspended process cannot run timers.
		// If BackgroundLimit elapsed, close the session as CloseForBackground would, then reconnect below.
		if (away && !closed && Environment.TickCount64 - _backgroundSince >= BackgroundLimit.TotalMilliseconds)
		{
			Disconnect();
			closed = true;
		}
		if (!closed && State == SessionState.Connected)
		{
			if (_core is { } core) { _ = CheckAliveAsync(core, back); }
			return;
		}
		_ = ConnectAsync(CancellationToken.None);
	}

	// Let queued frames run first. If none arrived, probe the same core instead of waiting for its next heartbeat.
	private async Task CheckAliveAsync(CoreConnection core, long back)
	{
		await Task.Yield();
		if (_core != core || State != SessionState.Connected) { return; }
		if (!core.Lost.IsCompleted && CoreEvents.LastFrame >= back) { return; }
		try
		{
			await core.CallAsync("doc.get", new() { ["name"] = "herdr-remote-resume-probe" }, CancellationToken.None, ResumeGrace);
		}
		catch (Exception ex)
		{
			if (_core != core || State != SessionState.Connected) { return; }
			Debug.WriteLine($"[Herdr] resume probe failed: {ex.Message}; connecting again");
			_ = ConnectAsync(CancellationToken.None);
		}
	}

	// Close cleanly when background time expires: sockets suspended by iOS can fail without notification.
	private void CloseForBackground(CancellationToken away)
	{
		if (away.IsCancellationRequested) { return; }
		_background?.Cancel();
		_closedInBackground = true;
		Disconnect();
		BackgroundTime.End();
	}

	// Close everything. Preserve an explicit user disconnect; otherwise publish Disconnected.
	public void Disconnect()
	{
		Close();
		if (State != SessionState.DisconnectedByOwner) { SetState(SessionState.Disconnected, null); }
	}

	// An explicit user disconnect closes everything until the user connects again.
	public void DisconnectByOwner()
	{
		Close();
		SetState(SessionState.DisconnectedByOwner, null, true);
	}

	// Cancel the retry loop and current attempt, then close the connection. Allow the caller's state through the gate.
	private void Close()
	{
		_retry?.Cancel();
		_attempt?.Cancel();
		_recovering = false;
		Teardown();
	}

	// While this core is current and Connected, check its last frame every WatchdogPeriod.
	// WatchdogSilence without a frame, including heartbeats, counts as a drop. Stop when this connection ends.
	private async Task WatchAsync(CoreConnection core, CancellationToken lifetime)
	{
		long since = Environment.TickCount64;
		using PeriodicTimer timer = new(WatchdogPeriod);
		try
		{
			while (await timer.WaitForNextTickAsync(lifetime) && _core == core)
			{
				if (State != SessionState.Connected || Environment.TickCount64 - Math.Max(since, CoreEvents.LastFrame) < WatchdogSilence.TotalMilliseconds) { continue; }
				Debug.WriteLine("[Herdr] no frame from the core; reconnecting");
				Teardown();
				StartRecovery();
				return;
			}
		}
		catch (OperationCanceledException) { }
	}

	private void StartRecovery()
	{
		// Do not retry in the background: Android soon cuts network access and iOS suspends execution.
		// Close as for expired background time; returning to the foreground reconnects immediately.
		if (_background is not null)
		{
			_closedInBackground = true;
			Close();
			BackgroundTime.End();
			SetState(SessionState.Disconnected, null, true);
			return;
		}
		_retry?.Cancel();
		_retry = new CancellationTokenSource();
		_ = RecoverAsync(_retry.Token);
	}

	// Retry a full connection after each delay, staying Reconnecting until success or a non-transient result.
	// If all failures are transient, publish the last one. On cancellation, the cancelling caller publishes its own state.
	private async Task RecoverAsync(CancellationToken loop)
	{
		_recovering = true;
		SetState(SessionState.Reconnecting, null, true);
		try
		{
			foreach (TimeSpan delay in RetryDelays)
			{
				await Task.Delay(delay, loop);
				TaskCompletionSource<SessionState> result = _attemptResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
				await ConnectOnceAsync(null, loop);
				if (await result.Task.WaitAsync(loop) is not (SessionState.Unreachable or SessionState.Failed or SessionState.HerdrNotRunning)) { return; }
			}
		}
		catch (OperationCanceledException) when (loop.IsCancellationRequested)
		{
			return;
		}
		_recovering = false;
		SetState(_lastResult.State, _lastResult.Detail, true);
	}

	// Close without publishing state: terminal and events first, then core channel, then SSH.
	// This prevents intentional closure from appearing as a failure. Keep Terminal set so the next connection reattaches it.
	private void Teardown()
	{
		_lifetime.Cancel();
		_lifetime.Dispose();
		_lifetime = new CancellationTokenSource();
		Terminal?.Close();
		Agents.Stop();
		Api = null;
		if (_core is { } core)
		{
			_core = null;
			_ = core.DisposeAsync();
		}
		if (_client is not null)
		{
			_client.HostKeyReceived -= OnHostKeyReceived;
			_client.ErrorOccurred -= OnErrorOccurred;
			_client.Disconnect();
			_client.Dispose();
			_client = null;
		}
	}

	// POSIX single-quote escaping for every value put into a remote command line: close the quote, an escaped quote, reopen.
	internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

	// SSH.NET calls this synchronously on its own thread. Reject unknown or changed keys; the page requests trust and reconnects.
	private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
	{
		string offered = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
		e.CanTrust = offered == RemoteSettings.PinnedHostKey;
		OfferedHostKey = e.CanTrust ? null : offered;
	}

	// SSH.NET uses the client as sender. Ignore late errors from replaced or torn-down clients.
	private void OnErrorOccurred(object? sender, ExceptionEventArgs e)
	{
		if (sender is SshClient client && client == _client) { Fail(e.Exception, client); }
	}

	// Map SSH failures to session states; unrecognized errors become Failed with their message.
	// client is null before creation. SetState discards failures from a replaced client when dispatching to the UI thread.
	private void Fail(Exception ex, SshClient? client, bool password = false)
	{
		SessionState state = ex switch
		{
			SshConnectionException when OfferedHostKey is not null => RemoteSettings.PinnedHostKey.Length == 0 ? SessionState.HostKeyUnknown : SessionState.HostKeyChanged,
			SshAuthenticationException when password => SessionState.PasswordRefused,
			SshAuthenticationException => SessionState.AuthFailed,
			SocketException or SshOperationTimeoutException => SessionState.Unreachable,
			_ => SessionState.Failed,
		};
		Debug.WriteLine($"[Herdr] {state}: {ex.GetType().Name}: {ex.Message}");
		SetState(state, ex.Message, client: client);
	}

	// SSH.NET events and read completions run on its threads; publish state and StateChanged on the UI thread.
	// A transient failure while Connected or Reconnecting starts recovery; non-transient failures need user action.
	// During retries, suppress attempt-start states and retain transient results. Publish success or a non-transient result.
	// direct bypasses the gate for Reconnecting, the final retry result and explicit disconnect.
	// Discard a client-scoped failure if that client is no longer current.
	private void SetState(SessionState state, string? detail, bool direct = false, SshClient? client = null) => MainThread.BeginInvokeOnMainThread(() =>
	{
		if (client is not null && client != _client) { return; }
		bool transient = state is SessionState.Unreachable or SessionState.Failed or SessionState.HerdrNotRunning;
		if (!direct && _recovering)
		{
			if (state is SessionState.Connecting or SessionState.Disconnected) { return; }
			if (transient) { _lastResult = (state, detail); }
			else { _recovering = false; }
			_attemptResult?.TrySetResult(state);
			if (transient) { return; }
		}
		else if (!direct && transient && State is SessionState.Connected or SessionState.Reconnecting)
		{
			StartRecovery();
			return;
		}
		State = state;
		Detail = detail;
		StateChanged?.Invoke();
	});
}

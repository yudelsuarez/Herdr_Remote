using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace HerdrRemote.Remote;

// Result of a document edit: Written saves a new version; Unchanged writes nothing because the edit has no effect or its
// target is gone; Conflicted writes nothing after three compare-and-set conflicts.
internal enum DocEdit { Written, Unchanged, Conflicted }

internal sealed record ShellFacts(string Shell, string Home);

// The scope of a close operation: pane, tab or space.
internal enum CloseLevel { Pane, Tab, Space }

// A close operation following herdr-app rules: scope, space, tab (null for a space), and terminals before adding sub-agents.
// Space closures also include tab and pane counts for the "Close space?" confirmation.
internal sealed record ClosePlan(CloseLevel Level, AgentSpace Space, AgentTab? Tab, List<string> Terminals, int Tabs, int Panes);

// Shared creation and layout operations for one connection: fresh IDs, cached shell facts, current space folders,
// compare-and-set edits to herdr-app's document, and terminals this phone started in the last 30 s. No UI dependency.
internal sealed class HerdrCreate(CoreApi api, HerdrAgents agents, bool windows)
{
	private const string Document = "herdr-app";
	private const int EditTries = 3;
	private const string WindowsShell = "powershell.exe";
	// The core launches its own processes, independently of the SSH login shell.
	private static List<string> WindowsArgv(string script, string shell = WindowsShell, bool loadProfile = false) =>
		[shell, "-NoLogo", loadProfile ? "-NonInteractive" : "-NoProfile", "-EncodedCommand", SshRemoteCommands.EncodePowerShell(script)];

	// Match herdr-core's built-in Windows shell: the prompt syncs the process cwd after the user's prompt runs.
	private const string WindowsPrompt = """
		$global:__hc_prompt = $function:prompt; function global:prompt { $p = & $global:__hc_prompt; $l = $ExecutionContext.SessionState.Path.CurrentLocation; if ($l.Provider.Name -eq 'FileSystem') { try { [Environment]::CurrentDirectory = $l.ProviderPath } catch {} }; $p }
		""";

	// Keep the POSIX argv byte-for-byte and list at most 500 visible directory names, separated by NUL.
	private const string BrowseScript = "n=0; for p in \"$1\"/*; do [ -d \"$p\" ] || continue; printf \"%s\\0\" \"${p##*/}\"; n=$((n+1)); [ \"$n\" -ge 500 ] && break; done; test -d \"$1\"";
	private const string WindowsBrowse = SshRemoteCommands.Utf8Console + """
		$ErrorActionPreference = 'Stop'
		$ProgressPreference = 'SilentlyContinue'
		trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
		$reader = [IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.Encoding]::UTF8)
		$dir = $reader.ReadToEnd()
		if (!(Test-Path -LiteralPath $dir -PathType Container)) { exit 1 }
		Get-ChildItem -LiteralPath $dir -Directory | Where-Object { !$_.Name.StartsWith('.') } |
		    Select-Object -First 500 | ForEach-Object { [Console]::Write($_.Name + [char]0) }
		exit 0
		""";

	// Resolve native commands and .cmd shims before .ps1 shims, which can be blocked by the host's execution policy.
	private const string FindWindowsAgent = """
		function Find-Agent($name) {
		    $entry = Get-Command -Name $name -CommandType Application -ErrorAction SilentlyContinue |
		        Where-Object { [IO.Path]::GetExtension($_.Path) -in '.exe', '.com', '.cmd', '.bat' } | Select-Object -First 1
		    if (!$entry) { $entry = Get-Command -Name ($name + '.ps1') -CommandType ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1 }
		    $entry
		}
		""";
	// Track newly launched terminals for the same time window as herdr-app.
	private static readonly TimeSpan BornFor = TimeSpan.FromSeconds(30);

	// The UI thread adds launches and the stream-end handler reads them; lock every access.
	private readonly Dictionary<string, long> _born = [];
	private readonly Lock _bornLock = new();

	private Task<ShellFacts>? _facts;

	// Match herdr-app IDs: a prefix (`sp` space, `tb` tab, `p` pane block, `s` split container) plus 32 lowercase hex digits.
	public static string NewId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

	// Read shell and home once per connection. Retry on the next request if the read fails.
	public Task<ShellFacts> FactsAsync(CancellationToken ct)
	{
		if (_facts is { IsFaulted: false, IsCanceled: false } facts) { return facts; }
		return _facts = ReadFactsAsync(ct);
	}

	private async Task<ShellFacts> ReadFactsAsync(CancellationToken ct)
	{
		IReadOnlyList<string> argv = windows ? WindowsArgv(SshRemoteCommands.Utf8Console + """
			$pwsh = Get-Command pwsh.exe -CommandType Application -ErrorAction Ignore | Select-Object -First 1
			$shell = if ($pwsh) { $pwsh.Source } else { [IO.Path]::Combine($PSHOME, 'powershell.exe') }
			[Console]::WriteLine($shell.Replace([char]92, [char]47)); [Console]::Write($HOME)
			""")
			: ["sh", "-c", "printf \"%s\\n%s\" \"$SHELL\" \"$HOME\""];
		CoreExec result = await api.ExecAsync(argv, null, 10, ct).ConfigureAwait(false);
		if (result.Code != 0) { throw new InvalidDataException($"Reading the remote shell and home failed (exit {result.Code}): {result.Error.Trim()}"); }
		if (windows)
		{
			string[] facts = result.Out.Split('\n', 2);
			string executable = facts[0].TrimEnd('\r');
			// A forward slash prevents terminal.run from treating the executable as a template name.
			if (!RemotePath.IsWindows(executable) || !executable.Contains('/')) { throw new InvalidDataException("The machine reported no PowerShell path."); }
			return facts is [_, { Length: > 0 } profile] ? new ShellFacts(executable, profile)
				: throw new InvalidDataException("The machine reported no home folder.");
		}
		string[] lines = result.Out.Split('\n');
		string shell = lines[0].Trim();
		string home = lines.Length > 1 ? lines[1].Trim() : "";
		if (home.Length == 0) { throw new InvalidDataException("The machine reported no home folder."); }
		return new ShellFacts(shell.Length > 0 ? shell : "/bin/sh", home);
	}

	internal async Task<List<string>?> ListFoldersAsync(string dir, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		CoreExec result = windows
			? await api.ExecAsync(WindowsArgv(WindowsBrowse), null, 10, ct, Encoding.UTF8.GetBytes(dir)).ConfigureAwait(false)
			: await api.ExecAsync(["sh", "-c", BrowseScript, "browse", dir], null, 10, ct).ConfigureAwait(false);
		return result.Code == 0 ? [.. result.Out.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : null;
	}

	// Resolve a space's current folder when the action runs, matching herdr-app: first live terminal's folder,
	// then the document's folder, then home.
	public async Task<string> WhereSpaceIsAsync(string spaceId, CancellationToken ct)
	{
		if (agents.Arranged.Find(space => space.Id == spaceId) is { Terminals: [var first, ..] } && agents.FolderOf(first.Address) is { Length: > 0 } live) { return live; }
		if (HerdrLayout.Spaces(agents.LayoutDocument).FirstOrDefault(space => HerdrLayout.Id(space) == spaceId)?["folder"]?.GetValue<string>() is { Length: > 0 } folder) { return folder; }
		return (await FactsAsync(ct).ConfigureAwait(false)).Home;
	}

	// Read the latest document and edit it in place. `edit` receives null for an unwritten document; false skips the write.
	// Write using compare-and-set; on a version conflict, reread and reapply the edit, up to three attempts.
	// Other failures (connection loss, timeout or core errors) propagate.
	public async Task<DocEdit> EditDocumentAsync(Func<JsonNode?, bool> edit, CancellationToken ct)
	{
		for (int attempt = 0; attempt < EditTries; attempt++)
		{
			CoreDoc doc = await api.DocReadAsync(Document, ct).ConfigureAwait(false);
			if (doc.Value is null || !edit(doc.Value)) { return DocEdit.Unchanged; }
			try
			{
				await api.DocSetAsync(Document, doc.Version, doc.Value, ct).ConfigureAwait(false);
				return DocEdit.Written;
			}
			catch (CoreRpcException ex) when (ex.Kind == "conflict")
			{
				Debug.WriteLine($"[Herdr] doc.set conflict on version {doc.Version}, try {attempt + 1}");
			}
		}
		return DocEdit.Conflicted;
	}

	// Record a new terminal for launch-failure notifications.
	public void Born(string address)
	{
		lock (_bornLock) { _born[address] = Environment.TickCount64; }
	}

	// Match herdr-app launch-failure reporting: a terminal started by this phone in the last 30 s exits on its own with a nonzero
	// code within 2 s. Return "Exit code N" and its last five non-empty lines. Remove it from the launch set so this is checked once;
	// return null for all other endings.
	public async Task<string?> LaunchFailureAsync(string address, CancellationToken ct)
	{
		if (!TakeBorn(address)) { return null; }
		JsonObject record = await api.GetAsync(address, ct).ConfigureAwait(false);
		if (record["reason"]?.GetValue<string>() != "exited" || record["exit_code"]?.GetValue<int>() is not { } code || code == 0
			|| record["finished_ms"]?.GetValue<long>() is not { } finished || record["created_ms"]?.GetValue<long>() is not { } created
			|| finished < created || finished - created > 2000) { return null; }
		CoreRead read = await api.ReadAsync(address, 5, ct).ConfigureAwait(false);
		List<string> lines = [.. read.Text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0)];
		string head = string.Format(System.Globalization.CultureInfo.CurrentCulture, Languages.i18n.Translate("Exit code {0}"), code);
		return lines.Count == 0 ? head : head + "\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 5)));
	}

	// Apply herdr-app close rules using live panes: a non-closable pane cannot close; closing the last pane closes its tab,
	// and closing the last tab closes its space. Panes include live terminals and mod panes; only tabs with such panes count.
	// Return null if the target is gone or has no space, since an unplaced terminal has no pane to close.
	public ClosePlan? PlanClose(string spaceId, string? tabId, string? pane)
	{
		if (agents.Arranged.Find(space => space.Id == spaceId && space.Id.Length > 0) is not { } space) { return null; }
		JsonObject? document = HerdrLayout.Spaces(agents.LayoutDocument).FirstOrDefault(s => HerdrLayout.Id(s) == spaceId);
		// The document's tabs of this space that hold a mod pane and no live terminal: they count as tabs (the phone lists none).
		List<JsonObject> modOnly = document is null ? [] : [.. HerdrLayout.Tabs(document)
			.Where(tab => !space.Tabs.Exists(t => t.Id == HerdrLayout.Id(tab)) && ModPanes(tab["root"] as JsonObject) > 0)];
		if (pane is not null)
		{
			if (space.Tabs.Find(t => t.Terminals.Exists(terminal => terminal.Address == pane)) is not { } paneTab) { return null; }
			if (paneTab.Root is { } root && HerdrLayout.FindBlock(root, pane)?["closable"] is JsonValue closable
				&& closable.TryGetValue(out bool can) && !can) { return null; }
			if (Panes(paneTab) > 1) { return new ClosePlan(CloseLevel.Pane, space, paneTab, [pane], 0, 0); }
			tabId = paneTab.Id;
		}
		if (tabId is not null)
		{
			if (space.Tabs.Find(t => t.Id == tabId) is not { } tab) { return null; }
			if (space.Tabs.Count + modOnly.Count > 1) { return new ClosePlan(CloseLevel.Tab, space, tab, [.. tab.Terminals.Select(t => t.Address)], 0, 0); }
		}
		return new ClosePlan(CloseLevel.Space, space, null, [.. space.Terminals.Select(t => t.Address)],
			space.Tabs.Count + modOnly.Count, space.Tabs.Sum(Panes) + modOnly.Sum(tab => ModPanes(tab["root"] as JsonObject)));
	}

	// A tab's panes: its live terminals (in the tree or not yet placed) and its mod panes.
	private static int Panes(AgentTab tab) => tab.Terminals.Count + ModPanes(tab.Root);

	private static int ModPanes(JsonObject? root) => root is null ? 0
		: HerdrLayout.Walk(root).Count(node => HerdrLayout.Type(node) == "block" && node["content"]?["mod"] is not null);

	// Match herdr-app's kill order: the requested terminals, then their live descendants recursively.
	// Include descendants only when no herdr.space label assigns them to a space (sub-agents).
	public List<string> KillSet(IEnumerable<string> terminals)
	{
		// Every live terminal with its parent and whether a space keeps it (the Mac's own group, Id "", holds the unlabelled ones).
		List<(string Address, string? Parent, bool Kept)> live = [.. agents.Arranged
			.SelectMany(space => space.Terminals.Select(t => (t.Address, t.Parent, Kept: space.Id.Length > 0)))];
		List<string> doomed = [.. terminals.Distinct()];
		for (int i = 0; i < doomed.Count; i++)
		{
			string parent = doomed[i];
			doomed.AddRange(live.Where(t => t.Parent == parent && !t.Kept && !doomed.Contains(t.Address)).Select(t => t.Address).ToList());
		}
		return doomed;
	}

	// Whether this phone started the terminal in the last 30 s.
	public bool IsBorn(string address)
	{
		lock (_bornLock)
		{
			ForgetOld();
			return _born.ContainsKey(address);
		}
	}

	// Check and remove a terminal from the recent-launch set atomically, so each launch is reported at most once.
	private bool TakeBorn(string address)
	{
		lock (_bornLock)
		{
			ForgetOld();
			return _born.Remove(address);
		}
	}

	// Remove launches older than 30 s. The caller holds the lock.
	private void ForgetOld()
	{
		long now = Environment.TickCount64;
		foreach (string old in _born.Where(pair => now - pair.Value > BornFor.TotalMilliseconds).Select(pair => pair.Key).ToList()) { _born.Remove(old); }
	}

	// Start a terminal via CoreApi.RunAsync, let herdr-app adopt it, and record the launch.
	// If an inherited folder (from a pane or space) fails with spawn_failed, retry once in home, matching herdr-app.
	// Never replace an explicitly chosen folder or retry when the holder did not respond: the terminal may still appear.
	public async Task<string> RunAsync(IReadOnlyList<string> argv, string folder, bool chosen, string spaceId, string tabId, (int Cols, int Rows)? size, JsonObject? display, CancellationToken ct)
	{
		string address;
		try
		{
			address = await api.RunAsync(argv, folder, spaceId, tabId, size, display, ct).ConfigureAwait(false);
		}
		catch (CoreRpcException ex) when (!chosen && ex.Kind == "spawn_failed" && !ex.Message.Contains("the holder did not answer", StringComparison.Ordinal))
		{
			Debug.WriteLine($"[Herdr] run in {folder} failed ({ex.CoreMessage}); once more in home");
			address = await api.RunAsync(argv, (await FactsAsync(ct).ConfigureAwait(false)).Home, spaceId, tabId, size, display, ct).ConfigureAwait(false);
		}
		Born(address);
		return address;
	}

	// The machine's shell runs the agent command, then stays interactive.
	// A missing command leaves the shell open with its own error message.
	internal async Task<List<string>> AgentArgvAsync(IReadOnlyList<string> run, CancellationToken ct)
	{
		ShellFacts facts = await FactsAsync(ct).ConfigureAwait(false);
		if (windows)
		{
			string command = SshRemoteCommands.PowerShellValue(run[0]);
			string arguments = string.Join("; ", run.Skip(1).Select(SshRemoteCommands.PowerShellValue));
			string script = WindowsPrompt + "\n" + FindWindowsAgent + "\n" + $$"""
				$name = {{command}}
				$entry = Find-Agent $name
				if ($entry) {
				    $agentArgs = @({{arguments}})
				    if ($agentArgs -ccontains '--no-daemon') {
				        try {
				            $global:LASTEXITCODE = 0
				            $helpLines = & $entry.Path '--help' 2>&1
				            if ($? -and $LASTEXITCODE -eq 0) {
				                $helpText = $helpLines -join "`n"
				                if ($helpText.Trim() -and $helpText -cnotmatch '(?<![\w-])--no-daemon(?![\w-])') {
				                    $agentArgs = @($agentArgs | Where-Object { $_ -cne '--no-daemon' })
				                }
				            }
				        } catch { }
				    }
				    & $entry.Path @agentArgs
				} else { Get-Command -Name $name -CommandType Application }
				""";
			return [facts.Shell, "-NoExit", "-Command", script];
		}
		string shell = facts.Shell;
		bool fish = Path.GetFileName(shell) == "fish";
		return [shell, "-lic", fish ? "command $argv[2..]; exec $argv[1] -li" : "\"$@\"; exec \"$0\" -li", shell, .. run];
	}

	// Cache installed-agent commands for this connection, matching herdr-app's 60 s cache.
	private static readonly TimeSpan InstalledFor = TimeSpan.FromSeconds(60);
	private List<string>? _installed;
	private long _installedAt;
	private Task<List<string>>? _checking;

	// Commands found by the last check, or null before the first successful check on this connection.
	public List<string>? Installed => _installed;

	// Whether the cached result is less than 60 s old.
	public bool InstalledFresh => _installed is not null && Environment.TickCount64 - _installedAt < InstalledFor.TotalMilliseconds;

	// Ask which agents the login environment can run, with only one check in flight.
	// Require exit code 0 and a herdr-check-complete line; read commands from herdr-has: lines.
	// Failures propagate without replacing the last successful result.
	public Task<List<string>> CheckInstalledAsync(CancellationToken ct)
	{
		if (_checking is { IsCompleted: false } running) { return running; }
		return _checking = CheckAsync(ct);
	}

	private async Task<List<string>> CheckAsync(CancellationToken ct)
	{
		ShellFacts facts = await FactsAsync(ct).ConfigureAwait(false);
		List<string> commands = [.. HerdrAgents.Startable.Select(agent => agent.Run[0]).Distinct()];
		CoreExec result;
		if (windows)
		{
			string names = string.Join("; ", commands.Select(SshRemoteCommands.PowerShellValue));
			result = await api.ExecAsync(WindowsArgv(SshRemoteCommands.Utf8Console + FindWindowsAgent + "\n" + $$"""
				foreach ($name in @({{names}})) { if (Find-Agent $name) { [Console]::WriteLine('herdr-has:' + $name) } }
				[Console]::WriteLine('herdr-check-complete')
				""", facts.Shell, loadProfile: true), null, 30, ct).ConfigureAwait(false);
		}
		else { result = await CheckUnixAsync(facts, commands, ct).ConfigureAwait(false); }
		string output = result.Out;
		if (result.Code != 0 || !output.Split('\n').Any(line => line.TrimEnd('\r') == "herdr-check-complete"))
		{
			throw new InvalidDataException($"could not check installed agents (exit {result.Code}): {result.Error.Trim()}");
		}
		HashSet<string> found = [.. output.Split("herdr-has:").Skip(1)
			.Select(rest => rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()).OfType<string>()];
		_installed = [.. commands.Where(found.Contains)];
		_installedAt = Environment.TickCount64;
		return _installed;
	}

	private Task<CoreExec> CheckUnixAsync(ShellFacts facts, List<string> commands, CancellationToken ct)
	{
		bool fish = Path.GetFileName(facts.Shell) == "fish";
		// Check commands in the shell itself: functions count, but aliases do not because "$@" cannot invoke them.
		// Use marked lines so startup output from an interactive shell does not obscure the results.
		string checks = string.Join(" ", commands.Select(command =>
		{
			string word = "'" + command.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
			string unalias = fish ? "" : $"unalias -- {word} 2>/dev/null; ";
			return $"{unalias}command -v {word} >/dev/null 2>&1 && printf 'herdr-has:%s\\n' {word};";
		}));
		return api.ExecAsync([facts.Shell, "-lic", $"{checks} printf '\\nherdr-check-complete\\n'"], null, 30, ct);
	}
}

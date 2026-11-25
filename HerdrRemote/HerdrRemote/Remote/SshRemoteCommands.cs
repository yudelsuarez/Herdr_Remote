using System.Globalization;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace HerdrRemote.Remote;

internal enum SshShell { Posix, PowerShell, Cmd }

internal static class SshRemoteCommands
{
	private const string ProbePrefix = "herdr-shell:";
	// sh expands $? to a numeric status, PowerShell to a Boolean, and cmd leaves it literal.
	internal const string Probe = "echo " + ProbePrefix + "$?";
	internal const string ProbeFailure = "Could not detect the SSH shell. Check whether the server forces a command.";

	internal static async Task<SshShell> ProbeAsync(SshClient client, CancellationToken ct)
	{
		using SshCommand probe = client.CreateCommand(Probe);
		// A server that forces another command may wait for input instead of replying.
		probe.CommandTimeout = TimeSpan.FromSeconds(3);
		try { await probe.ExecuteAsync(ct).ConfigureAwait(false); }
		catch (SshOperationTimeoutException ex) { throw new InvalidDataException(ProbeFailure, ex); }
		return probe.ExitStatus == 0 ? ParseShell(probe.Result) : throw new InvalidDataException(ProbeFailure);
	}

	internal static SshShell ParseShell(string reply)
	{
		ReadOnlySpan<char> marker = reply.Trim().AsSpan();
		if (marker.StartsWith(ProbePrefix, StringComparison.Ordinal))
		{
			ReadOnlySpan<char> status = marker[ProbePrefix.Length..];
			if (status is "True" or "False") { return SshShell.PowerShell; }
			if (status is "$?") { return SshShell.Cmd; }
			if (status.Length is >= 1 and <= 3 && status.IndexOfAnyExceptInRange('0', '9') < 0
				&& byte.TryParse(status, NumberStyles.None, CultureInfo.InvariantCulture, out _)) { return SshShell.Posix; }
		}
		throw new InvalidDataException(ProbeFailure);
	}

	internal static string EncodePowerShell(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
	internal const string Utf8Console = "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); ";

	// Keep path and argument values out of PowerShell syntax, including typographic quotes.
	internal static string PowerShellValue(string value) =>
		$"[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{EncodePowerShell(value)}'))";

	private static string PowerShell(string script)
	{
		// Encoded commands otherwise serialize PowerShell errors and progress as CLIXML on stderr.
		string wrapped = $$"""
			$ErrorActionPreference='Stop'
			$ProgressPreference='SilentlyContinue'
			trap { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
			{{script}}
			""";
		string command = $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {EncodePowerShell(wrapped)}";
		// Leave room below cmd.exe's 8191-character limit for OpenSSH's wrapper.
		if (command.Length > 7900) { throw new ArgumentException("The command exceeds the Windows shell limit."); }
		return command;
	}

	internal static string Enroll(bool windows, string line)
	{
		if (!windows) { return $"""set -e; umask 077; d="$HOME/.ssh"; f="$d/authorized_keys"; mkdir -p "$d"; chmod 700 "$d"; touch "$f"; chmod 600 "$f"; if ! grep -qF -- {Quote(line.Split(' ')[1])} "$f"; then if [ -s "$f" ] && [ -n "$(tail -c 1 "$f")" ]; then printf '\n' >> "$f"; fi; printf '%s\n' {Quote(line)} >> "$f"; fi"""; }
		// Use the stock OpenSSH paths only. The key-only reconnect verifies the server actually accepts the key.
		// Read the key as data so neither its contents nor its length affect the encoded shell command.
		return PowerShell("""
			$line = [IO.StreamReader]::new([Console]::OpenStandardInput()).ReadToEnd()
			if ($line -cnotmatch '\Assh-ed25519 ([A-Za-z0-9+/]+={0,2})(?: [^\r\n\0]*)?\z') { throw 'Invalid public key.' }
			$blob = [regex]::Escape($Matches[1])
			$id = [Security.Principal.WindowsIdentity]::GetCurrent()
			$admin = 'S-1-5-32-544'
			$system = 'S-1-5-18'
			$user = $id.User.Value
			$isAdmin = $id.Groups.Value -contains $admin
			$allowed = @($admin, $system)
			$sddl = 'D:P(A;;FA;;;SY)(A;;FA;;;BA)'
			if ($isAdmin) {
				$dir = [IO.Path]::Combine($env:ProgramData, 'ssh')
				$path = "$dir\administrators_authorized_keys"
				$sddl = "O:BA$sddl"
			} else {
				$dir = [IO.Path]::Combine($env:USERPROFILE, '.ssh')
				$path = "$dir\authorized_keys"
				$sddl = "O:$user$sddl(A;;FA;;;$user)"
				$allowed += $user
				[void][IO.Directory]::CreateDirectory($dir)
			}
			$link = [IO.FileAttributes]::ReparsePoint
			if ([IO.File]::GetAttributes($dir) -band $link) { throw 'Unexpected SSH key directory.' }
			try { $attributes = [IO.File]::GetAttributes($path) }
			catch [IO.FileNotFoundException] { $attributes = 0 }
			if ($attributes -band $link) { throw 'Unexpected SSH key file.' }
			$acl = [Security.AccessControl.FileSecurity]::new()
			$acl.SetSecurityDescriptorSddlForm($sddl)
			$stream = [IO.FileStream]::new($path, 'OpenOrCreate', 'Read,Write', 'Read', 4096, 'None', $acl)
			try {
				$badAcl = 'Unexpected SSH key file permissions.'
				$acl = $stream.GetAccessControl()
				$sid = [Security.Principal.SecurityIdentifier]
				$rules = $acl.GetAccessRules($true, $true, $sid)
				if ($acl.GetOwner($sid).Value -notin ($allowed + $user) -or !$rules.Count) { throw $badAcl }
				$full = [Security.AccessControl.FileSystemRights]::FullControl
				$grants = @{}
				foreach ($rule in $rules) {
					$who = $rule.IdentityReference.Value
					if ($who -notin $allowed -or $rule.AccessControlType -ne 'Allow') { throw $badAcl }
					if ($who -ne $system -or $rule.FileSystemRights.HasFlag($full)) { $grants[$who] = $true }
				}
				if ($isAdmin -and (!$grants[$system] -or !$grants[$admin])) { throw $badAcl }
				$utf8 = [Text.Encoding]::UTF8
				$reader = [IO.StreamReader]::new($stream, $utf8, $false, 1024, $true)
				$pattern = '^[ \t]*(?<options>(?!#)(?:[^" \t]|"(?:\\.|[^"\\])*")+[ \t]+)?ssh-ed25519[ \t]+' + $blob + '(?:[ \t]|$)'
				try {
					while ($null -ne ($row = $reader.ReadLine())) {
						if ($row.IndexOf([char]0) -ge 0) { throw 'Invalid SSH key file encoding.' }
						if ($row -cmatch $pattern) {
							if ($Matches['options']) { throw 'The key is already installed with restricted options.' }
							return
						}
					}
				} finally { $reader.Dispose() }
				$prefix = ''
				if ($stream.Length -gt 0) {
					$stream.Position = $stream.Length - 1
					if ($stream.ReadByte() -ne 10) { $prefix = "`n" }
				}
				$bytes = $utf8.GetBytes($prefix + $line + "`n")
				$stream.Write($bytes, 0, $bytes.Length)
			} finally { $stream.Dispose() }
			""");
	}

	// A PowerShell login shell otherwise converts any nonzero native exit code to 1.
	// The stock installer always installs herdr-core.exe, but the hc.exe alias and SSH PATH are optional.
	internal static string CoreConnect(SshShell shell) => shell != SshShell.Posix ? PowerShell("""
		$hc = if ($env:LOCALAPPDATA) { [IO.Path]::Combine($env:LOCALAPPDATA, 'Programs\herdr-core\herdr-core.exe') }
		if (!$hc -or ![IO.File]::Exists($hc)) {
			$hc = (Get-Command herdr-core.exe -CommandType Application -ErrorAction Ignore).Source
			if (!$hc) { $hc = (Get-Command hc.exe -CommandType Application -ErrorAction Ignore).Source }
		}
		if (!$hc -or ![IO.File]::Exists($hc)) { [Console]::Error.WriteLine('hc not found'); exit 127 }
		& $hc connect
		exit $LASTEXITCODE
		""") + (shell == SshShell.PowerShell ? "; exit $LASTEXITCODE" : "")
		: """for c in "$(command -v hc)" "$HOME/.local/bin/hc" /opt/homebrew/bin/hc /usr/local/bin/hc; do case "$c" in /*) [ -x "$c" ] && exec "$c" connect;; esac; done; echo "hc not found" >&2; exit 127""";

	internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

namespace HerdrRemote.Remote;

// Folder paths belong to the remote filesystem, not the phone's System.IO.Path rules.
internal static class RemotePath
{
	private static readonly char[] WindowsSeparators = ['\\', '/'];

	// Used for absolute folders in core events, where only the path itself is available.
	internal static bool IsWindows(string path) => path.StartsWith(@"\\", StringComparison.Ordinal)
		|| path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsWindowsSeparator(path[2]);

	private static bool IsWindowsSeparator(char c) => c is '\\' or '/';

	private static int WindowsRoot(string path)
	{
		if (path.Length >= 3 && path[1] == ':' && IsWindowsSeparator(path[2])) { return 3; }
		if (path.Length < 2 || !IsWindowsSeparator(path[0]) || !IsWindowsSeparator(path[1])) { return 0; }
		// UNC paths stop at the share, not the server. Verbatim UNC paths add a prefix before the server.
		int start = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? 8 : 2;
		int server = path.IndexOfAny(WindowsSeparators, start);
		int share = server < 0 ? -1 : path.IndexOfAny(WindowsSeparators, server + 1);
		return share < 0 ? path.Length : share + 1;
	}

	private static int WindowsEnd(string path, int root)
	{
		int end = path.Length;
		while (end > root && IsWindowsSeparator(path[end - 1])) { end--; }
		return end;
	}

	internal static string Name(string path, bool windows)
	{
		if (!windows)
		{
			string trimmed = path.TrimEnd('/');
			return trimmed[(trimmed.LastIndexOf('/') + 1)..];
		}
		int root = WindowsRoot(path), end = WindowsEnd(path, root);
		return end == root ? path[..root] : path[(path.AsSpan(0, end).LastIndexOfAny('\\', '/') + 1)..end];
	}

	internal static string? Parent(string path, bool windows)
	{
		if (!windows)
		{
			int cut = path.TrimEnd('/').LastIndexOf('/');
			return path == "/" ? null : cut <= 0 ? "/" : path[..cut];
		}
		int root = WindowsRoot(path), end = WindowsEnd(path, root);
		if (end == root) { return null; }
		int split = path.AsSpan(0, end).LastIndexOfAny('\\', '/');
		return path[..Math.Max(split, root)];
	}

	internal static string Join(string folder, string name, bool windows) => windows
		? IsWindowsSeparator(folder[^1]) ? folder + name : folder + "\\" + name
		: folder == "/" ? "/" + name : folder + "/" + name;

	internal static string Tilde(string path, string home, bool windows)
	{
		if (!windows)
		{
			return path == home ? "~" : path.StartsWith(home + "/", StringComparison.Ordinal) ? "~" + path[home.Length..] : path;
		}
		string folder = path.Replace('/', '\\'), root = home.Replace('/', '\\').TrimEnd('\\');
		return folder.TrimEnd('\\').Equals(root, StringComparison.OrdinalIgnoreCase) ? "~"
			: folder.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[root.Length..] : path;
	}

	internal static List<string> CrumbParts(string shown, bool windows)
	{
		if (!windows)
		{
			List<string> parts = shown == "/" ? ["/"] : [.. shown.Split('/', StringSplitOptions.RemoveEmptyEntries)];
			if (shown.StartsWith('/') && shown != "/") { parts.Insert(0, "/"); }
			return parts;
		}
		int root = WindowsRoot(shown);
		List<string> chunks = [.. shown[root..].Split(WindowsSeparators, StringSplitOptions.RemoveEmptyEntries)];
		if (root > 0) { chunks.Insert(0, shown[..root]); }
		return chunks;
	}
}

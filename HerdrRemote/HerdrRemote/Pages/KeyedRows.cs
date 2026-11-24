namespace HerdrRemote.Pages;

// Rows keep their native parent through value and order changes; only new identities build, and only absent identities detach.
internal sealed class KeyedRows<TRow>
{
	private readonly Grid _host;
	private readonly Func<TRow, (string Kind, string Key)> _identity;
	private readonly Func<TRow, View> _build;
	private readonly Action<View, TRow> _update;
	private readonly Action<View>? _detached;

	// Logical order; retained native children stay parented and move by Grid.Row.
	private readonly List<(TRow Row, View View)> _entries = [];

	public KeyedRows(Grid host, Func<TRow, (string Kind, string Key)> identity, Func<TRow, View> build, Action<View, TRow> update, Action<View>? detached = null)
	{
		_host = host;
		_identity = identity;
		_build = build;
		_update = update;
		_detached = detached;
	}

	public void Show(IReadOnlyList<TRow> wanted)
	{
		// 1. Identity = (Kind, Key, n), n counting the earlier rows with the same Kind and Key (the two Note rows with Key "").
		List<(string, string, int)> wantedIds = Identities(wanted);
		HashSet<(string, string, int)> wantedSet = [.. wantedIds];

		// Cancel absent targets before removing their native views.
		List<(string, string, int)> entryIds = Identities(_entries.ConvertAll(entry => entry.Row));
		Dictionary<(string, string, int), (TRow Row, View View, int Index)> kept = [];
		for (int i = _entries.Count - 1; i >= 0; i--)
		{
			if (wantedSet.Contains(entryIds[i])) { kept[entryIds[i]] = (_entries[i].Row, _entries[i].View, i); }
			else { Detach(i); }
		}
		List<(TRow Row, View View)> next = new(wanted.Count);
		while (_host.RowDefinitions.Count < wanted.Count) { _host.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); }
		for (int i = 0; i < wanted.Count; i++)
		{
			View view;
			if (kept.TryGetValue(wantedIds[i], out var entry))
			{
				view = entry.View;
				if (!EqualityComparer<TRow>.Default.Equals(entry.Row, wanted[i])) { _update(view, wanted[i]); }
				if (entry.Index != i) { Grid.SetRow(view, i); }
			}
			else
			{
				view = _build(wanted[i]);
				Grid.SetRow(view, i);
				_host.Children.Add(view);
			}
			next.Add((wanted[i], view));
		}
		while (_host.RowDefinitions.Count > wanted.Count) { _host.RowDefinitions.RemoveAt(_host.RowDefinitions.Count - 1); }
		_entries.Clear();
		_entries.AddRange(next);
	}

	// Every row detached; the next Show builds all.
	public void Clear()
	{
		for (int i = _entries.Count - 1; i >= 0; i--) { Detach(i); }
		_host.RowDefinitions.Clear();
	}

	private void Detach(int index)
	{
		View view = _entries[index].View;
		_entries.RemoveAt(index);
		_detached?.Invoke(view);
		_host.Children.Remove(view);
	}

	private List<(string, string, int)> Identities(IReadOnlyList<TRow> rows)
	{
		Dictionary<(string, string), int> seen = [];
		List<(string, string, int)> ids = new(rows.Count);
		foreach (TRow row in rows)
		{
			(string kind, string key) = _identity(row);
			seen.TryGetValue((kind, key), out int n);
			seen[(kind, key)] = n + 1;
			ids.Add((kind, key, n));
		}
		return ids;
	}
}

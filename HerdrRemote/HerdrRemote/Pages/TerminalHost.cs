namespace HerdrRemote.Pages;

// Only the leaf uses the window context; Shell's disposable wrappers keep their own fragment context.
public sealed class TerminalHost : ContentView
{
	internal IMauiContext? RetainedContext { get; private set; }
	private object? _mapping;
	private Func<bool>? _attached;
	private Action? _detach;
	private bool _arranged;
	private TaskCompletionSource? _detaching;
	internal bool IsAttached => _attached?.Invoke() == true;
	internal bool IsLayoutReady => _arranged && IsAttached;
	internal event Action? Changed;

	internal void Bind(IMauiContext context)
	{
		if (RetainedContext is not null && RetainedContext != context) { throw new InvalidOperationException("The terminal belongs to another window context."); }
		RetainedContext = context;
	}

	internal void Map(object mapping, Func<bool> attached, Action detach)
	{
		_mapping = mapping;
		_attached = attached;
		_detach = detach;
		RequireArrangement();
	}

	internal void NativeChanged(object mapping, bool arranged = false)
	{
		if (_mapping != mapping) { return; }
		if (!IsAttached)
		{
			_arranged = false;
			_detaching?.TrySetResult();
			_detaching = null;
		}
		else if (arranged) { _arranged = true; }
		Changed?.Invoke();
	}

	internal void Unmap(object mapping)
	{
		if (_mapping != mapping) { return; }
		_mapping = null;
		_attached = null;
		_detach = null;
		_arranged = false;
		_detaching?.TrySetResult();
		_detaching = null;
		Changed?.Invoke();
	}

	internal void RequireArrangement()
	{
		_arranged = false;
		InvalidateMeasure();
		Changed?.Invoke();
	}

	internal Task WaitForDetachAsync()
	{
		if (!IsAttached) { return Task.CompletedTask; }
		_detaching ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
		return _detaching.Task;
	}

	internal void Detach() => _detach?.Invoke();
	internal void ClearContext()
	{
		try { Detach(); }
		finally
		{
			RetainedContext = null;
			if (_mapping is { } mapping) { Unmap(mapping); }
		}
	}
}

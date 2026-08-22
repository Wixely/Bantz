namespace Bantz.Input;

/// <summary>Registers global inputs that all activate one hold or toggle action.</summary>
public interface IGlobalInputService : IDisposable
{
    bool IsSupported { get; }
    bool Enabled { get; set; }
    event Action? Pressed;
    event Action? Released;
    event Action? TogglePressed;
    IDisposable Register(InputBinding binding);
    void SetToggleBinding(InputBinding? binding);
}

/// <summary>Creates the global input service supported by the current platform.</summary>
public static class GlobalInputService
{
    public static IGlobalInputService Create() => OperatingSystem.IsWindows()
        ? new WindowsGlobalInputService()
        : new UnsupportedGlobalInputService();
}

/// <summary>A Windows service for keyboard, mouse, and XInput bindings.</summary>
public sealed class WindowsGlobalInputService : IGlobalInputService
{
    private readonly object _sync = new();
    private readonly List<InputBinding> _bindings = [];
    private readonly WindowsHoldInputMonitor _monitor;
    private InputBinding? _toggleBinding;
    private bool _disposed;

    public WindowsGlobalInputService()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Global input monitoring is currently supported on Windows only.");
        }

        _monitor = new WindowsHoldInputMonitor(Snapshot, () => Enabled, ToggleSnapshot);
        _monitor.HotkeyPressed += OnPressed;
        _monitor.HotkeyReleased += OnReleased;
        _monitor.ShortcutTogglePressed += OnTogglePressed;
    }

    public bool IsSupported => true;
    public bool Enabled { get; set; } = true;
    public event Action? Pressed;
    public event Action? Released;
    public event Action? TogglePressed;

    public IDisposable Register(InputBinding binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(binding);
        var registered = binding.Copy();
        lock (_sync)
        {
            if (_bindings.Any(candidate => candidate.SameInput(registered)))
            {
                throw new InvalidOperationException("That global input is already registered.");
            }

            _bindings.Add(registered);
        }

        return new Registration(this, registered.Id);
    }

    public void SetToggleBinding(InputBinding? binding)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _toggleBinding = binding?.Copy();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _monitor.HotkeyPressed -= OnPressed;
        _monitor.HotkeyReleased -= OnReleased;
        _monitor.ShortcutTogglePressed -= OnTogglePressed;
        _monitor.Dispose();
        lock (_sync)
        {
            _bindings.Clear();
            _toggleBinding = null;
        }

        _disposed = true;
    }

    private InputBinding[] Snapshot()
    {
        lock (_sync)
        {
            return _bindings.Select(binding => binding.Copy()).ToArray();
        }
    }

    private InputBinding? ToggleSnapshot()
    {
        lock (_sync)
        {
            return _toggleBinding?.Copy();
        }
    }

    private void Unregister(string id)
    {
        lock (_sync)
        {
            _bindings.RemoveAll(binding => string.Equals(binding.Id, id, StringComparison.Ordinal));
        }
    }

    private void OnPressed() => Pressed?.Invoke();
    private void OnReleased() => Released?.Invoke();
    private void OnTogglePressed() => TogglePressed?.Invoke();

    private sealed class Registration(WindowsGlobalInputService owner, string id) : IDisposable
    {
        private WindowsGlobalInputService? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(id);
    }
}

internal sealed class UnsupportedGlobalInputService : IGlobalInputService
{
    public bool IsSupported => false;
    public bool Enabled { get; set; }
    public event Action? Pressed { add { } remove { } }
    public event Action? Released { add { } remove { } }
    public event Action? TogglePressed { add { } remove { } }

    public IDisposable Register(InputBinding binding) =>
        throw new PlatformNotSupportedException("Global input monitoring is currently supported on Windows only.");

    public void SetToggleBinding(InputBinding? binding)
    {
    }

    public void Dispose()
    {
    }
}

using System.Buffers.Binary;

namespace Bantz.Input;

/// <summary>
/// Global hold-to-talk input on Linux, read straight from the kernel's evdev nodes.
///
/// <para>There is no lower-level seam available: X11 and Wayland both deliver input to the focused
/// window, and a push-to-talk binding has to work while something else is focused. Reading the
/// device nodes is how every other push-to-talk application on Linux does it, and it is why the
/// user has to be able to open them — on a Steam Deck the controller can be, while the touchscreen
/// cannot.</para>
///
/// <para>The devices arrive as streams rather than paths so that the decoding can be driven with
/// synthetic events in a test, on a machine with none of this hardware attached.</para>
/// </summary>
public sealed class LinuxHoldInputMonitor : IDisposable
{
    /// <summary>
    /// One evdev event: a 16-byte timeval, then type, code and value. 24 bytes on 64-bit, which is
    /// the only shape Bantz ships for.
    /// </summary>
    internal const int EventSize = 24;

    private readonly Func<IReadOnlyList<InputBinding>> _bindingsProvider;
    private readonly Func<bool> _shortcutsEnabledProvider;
    private readonly Func<InputBinding?> _shortcutToggleBindingProvider;
    private readonly List<Stream> _devices = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _sync = new();
    private readonly List<Task> _readers = [];

    private readonly Dictionary<int, int> _axisDirections = [];

    private KeyboardModifiers _modifiers;
    private bool _capturing;
    private InputBinding? _held;
    private bool _disposed;

    /// <summary>Opens every readable device the kernel offers that reports keys or buttons.</summary>
    public LinuxHoldInputMonitor(
        Func<IReadOnlyList<InputBinding>> bindingsProvider,
        Func<bool> shortcutsEnabledProvider,
        Func<InputBinding?> shortcutToggleBindingProvider)
        : this(bindingsProvider, shortcutsEnabledProvider, shortcutToggleBindingProvider, OpenReadableDevices())
    {
    }

    /// <summary>Reads the streams given, which is the seam a test supplies its own events through.</summary>
    public LinuxHoldInputMonitor(
        Func<IReadOnlyList<InputBinding>> bindingsProvider,
        Func<bool> shortcutsEnabledProvider,
        Func<InputBinding?> shortcutToggleBindingProvider,
        IEnumerable<Stream> devices)
    {
        ArgumentNullException.ThrowIfNull(bindingsProvider);
        ArgumentNullException.ThrowIfNull(shortcutsEnabledProvider);
        ArgumentNullException.ThrowIfNull(shortcutToggleBindingProvider);
        ArgumentNullException.ThrowIfNull(devices);

        _bindingsProvider = bindingsProvider;
        _shortcutsEnabledProvider = shortcutsEnabledProvider;
        _shortcutToggleBindingProvider = shortcutToggleBindingProvider;

        _devices.AddRange(devices);
    }

    /// <summary>
    /// Begins reading. Separate from construction on purpose: reading starts delivering events
    /// immediately, and anything raised before the caller has subscribed is simply lost — which is
    /// a race with a real device and a certainty with a device that is already holding its events.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            if (_readers.Count > 0)
            {
                return;
            }

            foreach (var device in _devices)
            {
                _readers.Add(Task.Run(() => ReadAsync(device, _stopping.Token)));
            }
        }
    }

    public event Action? HotkeyPressed;
    public event Action? HotkeyReleased;
    public event Action? ShortcutTogglePressed;
    public event Action? LeftMouseReleased;
    public event Action<InputBinding>? BindingCaptured;
    public event Action? CaptureCancelled;

    /// <summary>How many devices are being read. Zero means nothing can ever fire.</summary>
    public int DeviceCount => _devices.Count;

    public bool BeginCapture()
    {
        lock (_sync)
        {
            if (_capturing || _held is not null)
            {
                return false;
            }

            _capturing = true;
            return true;
        }
    }

    public void CancelCapture()
    {
        var cancelled = false;
        lock (_sync)
        {
            if (_capturing)
            {
                _capturing = false;
                cancelled = true;
            }
        }

        if (cancelled)
        {
            CaptureCancelled?.Invoke();
        }
    }

    /// <summary>Every device node this user may open that reports keys or buttons.</summary>
    private static IEnumerable<Stream> OpenReadableDevices()
    {
        foreach (var device in LinuxInputDevices.List())
        {
            if (device.EventNode is not { } node ||
                !(device.HasGamepadButtons || device.HasKeyboardKeys || device.HasMouseButtons))
            {
                continue;
            }

            Stream? stream = null;
            try
            {
                stream = new FileStream(node, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, EventSize, useAsync: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A device this user cannot read is not a failure: the ones that matter on a Steam
                // Deck can be read, and the rest are somebody else's.
            }

            if (stream is not null)
            {
                yield return stream;
            }
        }
    }

    private async Task ReadAsync(Stream device, CancellationToken cancellationToken)
    {
        var buffer = new byte[EventSize * 16];
        var pending = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await device.ReadAsync(buffer.AsMemory(pending), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                pending += read;
                var offset = 0;
                // A read can split an event, so only whole ones are decoded and the remainder is
                // carried to the next read.
                while (pending - offset >= EventSize)
                {
                    Decode(buffer.AsSpan(offset, EventSize));
                    offset += EventSize;
                }

                if (offset > 0)
                {
                    Buffer.BlockCopy(buffer, offset, buffer, 0, pending - offset);
                    pending -= offset;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The device went away, or Bantz is shutting down. Either way this reader is finished.
        }
    }

    private void Decode(ReadOnlySpan<byte> raw)
    {
        var type = BinaryPrimitives.ReadUInt16LittleEndian(raw[16..]);
        var code = BinaryPrimitives.ReadUInt16LittleEndian(raw[18..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(raw[20..]);
        if (type == EvdevCodes.EventAbsolute)
        {
            HandleAxis(code, value);
            return;
        }

        if (type != EvdevCodes.EventKey || value == 2)
        {
            return; // not a key, or a repeat, which is neither a press nor a release
        }

        Handle(code, pressed: value == 1);
    }

    /// <summary>
    /// An axis held like a button. Axes report continuously, so only a change of direction is a
    /// press or a release; letting go of one direction releases it before the other is pressed, so
    /// a hat flicked straight across cannot leave both held.
    /// </summary>
    private void HandleAxis(int axis, int value)
    {
        if (!EvdevCodes.IsBindableAxis(axis))
        {
            return;
        }

        var direction = EvdevCodes.AxisDirection(axis, value);
        int previous;
        lock (_sync)
        {
            previous = _axisDirections.GetValueOrDefault(axis);
            if (direction == previous)
            {
                return;
            }

            _axisDirections[axis] = direction;
        }

        if (previous != 0)
        {
            Handle(EvdevCodes.AxisBindingCode(axis, previous), pressed: false);
        }

        if (direction != 0)
        {
            Handle(EvdevCodes.AxisBindingCode(axis, direction), pressed: true);
        }
    }

    private void Handle(int code, bool pressed)
    {
        var modifier = EvdevCodes.ModifierFor(code);
        if (modifier != KeyboardModifiers.None)
        {
            lock (_sync)
            {
                _modifiers = pressed ? _modifiers | modifier : _modifiers & ~modifier;
            }

            return; // a modifier on its own is never a binding
        }

        if (EvdevCodes.DeviceFor(code) is not { } device)
        {
            return;
        }

        if (!pressed && code == EvdevCodes.ButtonLeft)
        {
            LeftMouseReleased?.Invoke();
        }

        InputBinding? captured = null;
        var togglePressed = false;
        var startHold = false;
        var endHold = false;

        lock (_sync)
        {
            if (_capturing && pressed)
            {
                _capturing = false;
                captured = new InputBinding
                {
                    Device = device,
                    Code = (uint)code,
                    Modifiers = device == InputDevice.Keyboard ? _modifiers : KeyboardModifiers.None,
                    DisplayName = EvdevCodes.DescribeBinding(
                        device,
                        code,
                        device == InputDevice.Keyboard ? _modifiers : KeyboardModifiers.None),
                };
            }
            else if (pressed)
            {
                if (Matches(_shortcutToggleBindingProvider(), device, code))
                {
                    togglePressed = true;
                }
                else if (_held is null && _shortcutsEnabledProvider() &&
                         _bindingsProvider().FirstOrDefault(binding => Matches(binding, device, code)) is { } binding)
                {
                    _held = binding;
                    startHold = true;
                }
            }
            else if (_held is { } active && active.Device == device && active.Code == (uint)code)
            {
                _held = null;
                endHold = true;
            }
        }

        if (captured is not null)
        {
            BindingCaptured?.Invoke(captured);
        }

        if (togglePressed)
        {
            ShortcutTogglePressed?.Invoke();
        }

        if (startHold)
        {
            HotkeyPressed?.Invoke();
        }

        if (endHold)
        {
            HotkeyReleased?.Invoke();
        }
    }

    /// <summary>
    /// Modifiers are compared for keyboard bindings only: a gamepad button is the whole binding,
    /// and holding a controller while a Shift key happens to be down must not stop it matching.
    /// </summary>
    private bool Matches(InputBinding? binding, InputDevice device, int code) =>
        binding is not null &&
        binding.Device == device &&
        binding.Code == (uint)code &&
        (device != InputDevice.Keyboard || binding.Modifiers == _modifiers);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        foreach (var device in _devices)
        {
            device.Dispose();
        }

        try
        {
            Task.WaitAll([.. _readers], TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Readers end by being cancelled or by their device closing under them; either is fine.
        }

        _stopping.Dispose();
    }
}

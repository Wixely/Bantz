# Bantz.Input

Windows global input monitoring for keyboard chords, mouse buttons, and XInput
gamepad buttons. `WindowsHoldInputMonitor` provides press/release hold semantics,
shortcut-toggle events, and interactive binding capture. Query
`GlobalInputCapabilities.Current` before constructing the Windows monitor.

For the usual single-action push-to-talk case, `GlobalInputService.Create()` exposes
explicit register/unregister handles, aggregate press/release events, and a toggle
binding that remains active while normal bindings are disabled.

Tray UI is not part of this package's first public contract. Bantz's current tray
controller modifies a CupriFace-owned icon and cannot yet create an independent
icon, menu, or click surface without new platform code.

# Contributing

## Development checks

Install the .NET SDK selected by `global.json`, register the CupriFace feed credentials as
described in the README, and run:

```powershell
./eng/Verify.ps1
```

Pull requests must pass these required checks before merge:

- `quality`: formatting, NuGet audit, warnings-as-errors build, and tests;
- `package / Windows x64`: self-contained publish plus a headless UI smoke test;
- `package / Linux x64`: self-contained publish plus a headless UI smoke test;
- `dependency review`: rejects newly introduced vulnerable dependencies;
- `CodeQL`: C# static analysis.

## Trimming

Release publishes are trimmed (`PublishTrimmed` with `TrimMode=partial`), which halves the
self-contained executable. Partial mode trims only assemblies that declare themselves trimmable,
which in practice means the framework: AngleSharp, Silk.NET, and the CupriFace shell all resolve
types by reflection and are copied whole instead.

`SuppressTrimAnalysisWarnings` is on because those dependencies raise IL2xxx warnings this
repository cannot fix, and `TreatWarningsAsErrors` would otherwise fail the publish. Bantz's own
code stays trim-safe without needing the suppression: UI binding comes from the
`[CupriBindable]` source generator as a name switch, and settings use a `JsonSerializerContext`.
Keep it that way; reaching for reflection over app types would break under trimming without any
build warning.

Do not switch to `TrimMode=full` without testing the published binary. Full mode trims the
reflection-driven dependencies and reports COM interop warnings from the shell's UI Automation
bridge, which is what screen readers talk to.

Please keep platform behavior explicit. Windows is the primary supported host. Linux changes should be tested on both X11 and Wayland when they affect input or window integration.

## Releases

The version is set centrally in `Directory.Build.props`. A tag must match it exactly with a `v` prefix, such as `v0.1.0`. Pushing that tag runs the full verification and packaging gates before creating a prerelease with direct standalone executables, Windows and Linux archives, and SHA-256 checksum files.

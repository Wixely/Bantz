# Contributing

## Development checks

Install the .NET SDK selected by `global.json`, set `CUPRIFACE_GITHUB_USER` and
`CUPRIFACE_GITHUB_TOKEN` as described in the README, and run:

```powershell
./eng/Verify.ps1
```

Pull requests must pass these required checks before merge:

- `quality`: formatting, NuGet audit, warnings-as-errors build, and tests;
- `package / Windows x64`: self-contained publish plus a headless UI smoke test;
- `package / Linux x64`: self-contained publish plus a headless UI smoke test;
- `dependency review`: rejects newly introduced vulnerable dependencies;
- `CodeQL`: C# static analysis.

Please keep platform behavior explicit. Windows is the primary supported host. Linux changes should be tested on both X11 and Wayland when they affect input or window integration.

## Releases

The version is set centrally in `Directory.Build.props`. A tag must match it exactly with a `v` prefix, such as `v0.1.0`. Pushing that tag runs the full verification and packaging gates before creating a prerelease with direct standalone executables, Windows and Linux archives, and SHA-256 checksum files.

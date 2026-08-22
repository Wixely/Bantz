# Bantz Package Split — Work Plan

## Implementation status (2026-08-22)

The plan is implemented on the active feature branch with four packable .NET 10 projects,
project-reference consumption by Bantz, package-specific READMEs and notices, symbol packages,
six package-contract tests, a package-only `MinimalDictation` consumer, PR pack verification,
and tagged GitHub Packages publication.

Source inspection changed three proposed boundaries:

- The 1.5-second accidental-press rule remains configurable `DictationWorkflow` policy; it is
  not a Whisper option. A consumer can pass a zero threshold or use its own VAD workflow.
- The equalizer and meaningful-sound detector moved together into `Bantz.Capture`, because they
  share the same PCM frame stream and analysis state.
- Generic tray support is deferred. The current Windows controller modifies a CupriFace-owned
  icon and cannot independently provide lifecycle, click, or context-menu behavior.

The initial libraries share the Bantz `0.2.3` version so one tag publishes the app and packages
transactionally. Model and runtime installation use cross-process file locks; the `base.en`
model and pinned runtime packages are integrity checked before promotion.

Plan for the Bantz repo: extract Bantz's reusable parts into NuGet packages on the **Wixely
GitHub Packages feed** so they can be consumed by other Wixely apps (first consumer: Banter, a
chat suite whose desktop client needs exactly Bantz's STT + capture + global-input stack), while
Bantz itself keeps working by consuming its own packages (dogfood).

This document is self-contained; no Banter repo access is needed. Where it says "Banter needs X",
treat X as the acceptance requirement for the public API.

## 1. Goal & non-goals

**Goal:** four reusable, app-agnostic packages cut along seams that already exist in the
code:

| Package | Contents (today's locations) |
|---|---|
| `Bantz.Speech.Abstractions` | `ITranscriptionEngine` + its request/result types. Zero heavy dependencies. |
| `Bantz.Speech.Whisper` | `WhisperTranscriptionEngine`, `WhisperRuntimeManager`, model download/verify/cache, and GPU(Vulkan)/CPU selection. Depends on Whisper.net + the abstractions package. |
| `Bantz.Capture` | Capture abstraction + `WindowsAudioRecorder` (`src/Bantz.Windows/Platform/Windows/`) and `LinuxAudioRecorder` (`src/Bantz.Linux/Platform/`), normalizing to 16 kHz mono PCM. |
| `Bantz.Input` | Global binding types + `WindowsHoldInputMonitor` (keyboard chords, XInput gamepad, mouse buttons; hold + toggle semantics) and platform capability reporting. |

**Non-goals — stays in Bantz, do not package:** text injection (`SendInput`, `LinuxTextInjector`
/ wtype / xdotool), the countdown-before-typing UX, the CupriFace UI, `AppSettings`/
`SettingsStore` (packages must take options objects, not read Bantz settings), and the app
executables.

**Why abstractions are a separate speech package:** consumers will write *remote* engines
(OpenAI-compatible HTTP, Wyoming TCP) against `ITranscriptionEngine` without wanting Whisper.net
or a 142 MiB model in their dependency graph. The contract must be free of Whisper types. (This
also means Bantz can later gain remote engines for free.)

## 2. Target repo layout

```
Bantz.slnx
├── src/
│   ├── Bantz.Speech.Abstractions/   (new, packable)
│   ├── Bantz.Speech.Whisper/        (new, packable)
│   ├── Bantz.Capture/               (new, packable)
│   ├── Bantz.Input/                 (new, packable)
│   ├── Bantz.Core/                  (existing — shrinks; app-only logic remains)
│   ├── Bantz.App/                   (existing — UI/settings/workflows; references the four above)
│   ├── Bantz.Windows/               (existing — platform head; keeps injection + app glue)
│   └── Bantz.Linux/                 (existing — platform head; keeps injection + app glue)
└── tests/
    ├── Bantz.Core.Tests/            (existing)
    └── Bantz.Packages.Tests/        (new — tests for the four packages, moved/added)
```

Inside the repo, apps consume the packages via `ProjectReference` (normal solution build);
external consumers use the published NuGet packages. Same code, no publish round-trip needed for
local development.

Platform strategy for `Bantz.Capture` / `Bantz.Input`: single package each, multi-targeted or
runtime-dispatched (keep whatever pattern the code already uses for choosing
Windows/Linux implementations; do not force consumers to reference per-OS packages). Linux
limitations carry over honestly: capture works, global bindings/tray are Windows-only today —
the abstraction should expose a capability query (e.g. `SupportsGlobalBindings`) rather than
throwing surprises.

## 3. Public API requirements (Banter's consumption contract)

Keep existing shapes wherever they already fit; the requirements below are the minimum. Renames
are fine — coherence over ceremony.

**`Bantz.Speech.Abstractions`**
- `ITranscriptionEngine` with an async transcribe accepting **16 kHz mono PCM** (buffer or
  stream), `CancellationToken` support, returning the transcript text (a result type with room
  to grow — e.g. optional segments/timestamps/language later — beats returning `string`).
- Engine lifecycle: async initialization separated from transcription (model load/download is
  slow; consumers need to warm up ahead of first use and query readiness).
- Progress/diagnostics hooks for long operations (model download %) as events or callbacks —
  no console writes, no UI assumptions inside the library.

**`Bantz.Speech.Whisper`**
- Options object covering model/cache path, runtime root, language, and GPU/CPU selection.
- Minimum duration remains a consumer workflow option and can be disabled with a zero threshold.
- Model download must verify integrity (keep whatever SHA validation exists) and be safe under
  concurrent first-run (two processes/instances racing the download).

**`Bantz.Capture`**
- Start/stop capture producing PCM frames as a push stream (event or `IAsyncEnumerable`) plus a
  capture-to-completion convenience (the current hold-to-talk "record until release" shape).
- Output normalized to 16 kHz mono regardless of device format.
- Device: default device is fine for v1, but surface the seam (options object with a device id
  field, even if only "default" is implemented today).

**`Bantz.Input`**
- Register/unregister global bindings: keyboard chords, XInput gamepad buttons, mouse buttons.
- **Hold semantics** (pressed/released events — consumers implement push-to-talk on top) and
  **toggle semantics**, both as today.
- Must work without a focused window (that's the point). Windows-only for now; capability query
  for the rest.
- Generic tray lifecycle, context-menu, and click support is deferred until a standalone
  implementation exists; the current CupriFace icon modifier remains app-owned.

**All packages:** no references to `Bantz.App`/`Bantz.Core`/settings; `net10.0` (+
`net10.0-windows` where required); nullable enabled; XML docs on public surface; no static
mutable state that prevents two independent instances in one process.

## 4. Work items (suggested order)

1. **Carve the abstractions.** Create `Bantz.Speech.Abstractions`; move `ITranscriptionEngine`
   and its types there (from `Bantz.Core` or wherever they live). Fix namespaces
   (`Bantz.Speech`, `Bantz.Capture`, `Bantz.Input` roots). Solution builds, tests pass.
2. **Extract `Bantz.Speech.Whisper`.** Move `WhisperTranscriptionEngine` +
   `WhisperRuntimeManager` + model management out of `Bantz.App/Transcription/`. Break any
   settings coupling by introducing the options object (§3); `Bantz.App` maps its
   `AppSettings` onto the options at construction.
3. **Extract `Bantz.Capture`.** Move the two recorders behind the capture abstraction; the
   platform heads keep only app glue. Same options-object treatment.
4. **Extract `Bantz.Input`.** Move `WindowsHoldInputMonitor` and binding types behind a
   capability query. Keep `WindowsTaskbarIconController` app-owned until an independent tray
   lifecycle, menu, and click implementation exists.
5. **Repoint the apps.** `Bantz.App`/`Bantz.Windows`/`Bantz.Linux` consume the four projects via
   `ProjectReference`. Behavior must be identical — this refactor ships a Bantz release with
   **zero user-visible changes** (the proof the split is clean).
6. **Package metadata.** In each packable csproj (or `Directory.Build.props` conditioned on the
   packable projects): `PackageId`, version aligned with the Bantz release, license (MIT), repo URL,
   `README.md` per package, symbols (`snupkg`), deterministic build. The initial version follows
   the Bantz app version so one tag releases both. **Third-party notices:**
   `Bantz.Speech.Whisper` must carry the Whisper.net + whisper.cpp MIT notices in the package,
   same as the app releases do today.
7. **CI/publish.** Extend `eng/Package.ps1` + `.github/workflows/release.yml`: on release tag,
   `dotnet pack` the four packages and push to the Wixely GitHub Packages feed
   (`GITHUB_TOKEN` with `packages:write` — same pattern CupriFace adopted in its PR #49).
   `ci.yml` gains a pack step (no push) so packability breakage is caught on every PR.
8. **Tests.** Move/extend coverage into `tests/Bantz.Packages.Tests`: transcription engine
   contract tests (a fake engine proves the abstraction is implementable without Whisper),
   capture normalization (input format → 16 kHz mono), input-binding semantics where testable
   headlessly, options validation, concurrent model-download safety.
9. **Consumer smoke sample.** A tiny console sample in the repo (`samples/MinimalDictation/`)
   that references the **packages** (not projects) from the feed: register a binding → capture →
   transcribe → print. This is both docs and the proof external consumption works.

## 5. Acceptance criteria

- [ ] Four packages on the Wixely feed, restorable with a `read:packages` PAT.
- [ ] `Bantz.Speech.Abstractions` has no Whisper.net (or other native) dependency.
- [ ] A consumer can implement `ITranscriptionEngine` with a trivial fake and run
      capture → engine → text without Whisper installed.
- [ ] Whisper engine runs with a custom model path; a consumer workflow runs with its
      minimum-duration filter disabled.
- [ ] `Bantz.Input` delivers press/release for a keyboard chord, an XInput button, and a mouse
      button while an unrelated window has focus.
- [ ] Bantz app release built from the refactored solution behaves identically to the previous
      release (manual pass of the standard dictation flow).
- [ ] CI packs on PR; release workflow publishes; packages include symbols, licenses, notices.

## 6. Notes for the Banter side (context, no action needed in Bantz)

Banter will: implement `ITranscriptionEngine` over OpenAI-compatible HTTP and Wyoming TCP;
use `Bantz.Speech.Whisper` as the desktop default engine; use `Bantz.Capture` on desktop;
use `Bantz.Input` for global push-to-talk (hold) bound to sending transcripts into a chat room.
That is why hold semantics, cancellation, disable-able min-length filter, and the
abstractions/implementation split are hard requirements rather than nice-to-haves.

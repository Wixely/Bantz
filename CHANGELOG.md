# Changelog

All notable changes to Bantz are recorded here. Versions follow `MAJOR.MINOR.PATCH`; while Bantz
is pre-1.0 a minor bump may change behaviour you rely on.

## Unreleased

### Added

- **Models tab** for choosing the speech model and language. Tiny, Base, Small, Medium and Large v3
  Turbo are offered, each multilingual and most with a faster English-only build. Choosing a model
  that is not downloaded fetches it; one that is not in use can be deleted.
- Transcription in languages other than English, using a multilingual model. Twenty languages are
  listed, along with letting Whisper detect the language itself. English-only models keep
  transcribing English whatever the setting says, because asking one for another language returns
  English text under the wrong label.
- `--download-model <id>`, which downloads one model and exits, for scripted or offline setup.

### Changed

- The model and language are read for each transcription, so changing either applies to the next
  one without a restart.
- Diagnostics reports the language and model in use rather than a fixed "English (en)".

### Known limitations

- Only the default Base (English) model is checked against a published size and hash. The others are
  checked for the ggml header and for matching the length the server declared, which is what can be
  verified without shipping a hash for every model.

## 0.3.0

### Added

- **Input tab** for choosing which microphone Bantz records from. The list refreshes when the tab
  opens and from its **Refresh** button. Bantz remembers the device by name, so it finds the same
  microphone again after Windows renumbers devices, and falls back to the system default while
  that microphone is disconnected.
- **Compatibility mode (clipboard paste)** on the settings page, for destinations that drop
  synthesized keystrokes — Remote Desktop sessions and some virtual-machine consoles. Bantz puts
  the transcript on the clipboard, sends Ctrl+V, and hands your clipboard back afterwards. On
  Windows every format is preserved, so copied text, images, and files all survive; images keep
  their exact pixels because the bitmap handle is duplicated rather than copied byte by byte.
- **PTT shortcuts toggle** on the first settings page, alongside the hotkey that already switched
  them on and off.
- `--list-inputs`, which writes a report of what each audio layer sees to `bantz-inputs.txt` beside
  the executable. Useful when a microphone is named or listed unexpectedly.

### Changed

- Release builds are trimmed, roughly halving the download: the Windows executable drops from
  64.4 MiB to 34.3 MiB and the Linux one from 51.7 MiB to 27.9 MiB.
- Microphone names come from the Core Audio endpoints instead of the wave-in API, which cut them
  to 31 characters — "Microphone (SteelSeries Arctis" is now "Microphone (SteelSeries Arctis 7
  Chat)". A microphone saved under a truncated name still matches after upgrading.
- The UI runtime moves to CupriFace 0.20.0.

### Fixed

- The diagnostics page no longer squeezes its status banner and rows to a couple of pixels, and the
  model path no longer draws over the **OPEN FOLDER** label.
- The advanced shortcuts dialog is centred on the window rather than at a fixed offset that only
  centred it at one size.
- The first-run setup page no longer clips the runtime descriptions or overlaps the model download
  box with the runtime cards.
- Buttons in the microphone list show the pointer cursor.
- Local builds authenticate to the CupriFace package feed again. The repository's `NuGet.config`
  named the source differently from the credentials developers store and shadowed them with
  placeholders, so restore failed with 401.

### Known limitations

- On Linux, compatibility mode preserves a single clipboard type rather than all of them, because
  `wl-copy` and `xclip` each own the selection for one type per invocation. It needs `wl-copy` and
  `wl-paste` on Wayland, or `xclip` on X11.
- Clipboard formats Bantz cannot duplicate — colour palettes, owner-drawn formats, and anything
  over 32 MiB — are dropped rather than restored.

## 0.2.4 and earlier

Earlier releases are described on the
[releases page](https://github.com/Wixely/Bantz/releases).

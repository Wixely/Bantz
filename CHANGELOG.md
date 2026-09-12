# Changelog

All notable changes to Bantz are recorded here. Versions follow `MAJOR.MINOR.PATCH`; while Bantz
is pre-1.0 a minor bump may change behaviour you rely on.

## Unreleased

### Added

- The interface engine moves to CupriFace 0.23.0, and Bantz gains `--doctor` and `--dump-tree` from
  it. The first reads the real engine and names markup and CSS it will quietly do nothing with —
  including contents too tall for a fixed-height box, which do not clip but paint over whatever
  follows, the fault behind two of the layout bugs fixed above. The second prints the laid-out tree
  with absolute positions, which is how those were measured. Every page renders identically to
  0.20.0, checked against a render of each before the upgrade.
- **Global hold-to-talk on Linux**, including gamepad buttons — which is the point of Bantz on a
  Steam Deck. There was no implementation at all before: the service that registers global inputs
  returned a stub off Windows whose every registration did nothing, and the Linux host wired no
  capture, so the Keybinds tab looked like it worked and bound nothing, whatever device you pressed.
  Bantz now reads the kernel's evdev devices directly, which is the only seam that sees input while
  another window is focused. On a Steam Deck the controller Steam presents can be read without
  privileges; devices this user cannot open are skipped rather than failing the rest.
- `--watch-input` on the Linux build, which prints every key and axis event the readable devices
  report. A button that binds nowhere is either invisible to the kernel, in which case no
  application can have it, or it arrives as a code Bantz does not offer yet — and this says which.
- `--list-hid` on the Linux build, which reports every input device the kernel offers, what it can
  report, and whether Bantz may read it — the question that decides whether global input can work
  on a given machine.
- Every language Whisper knows, rather than a curated nineteen. Norwegian, Greek, Hebrew, Thai,
  Welsh and seventy-odd others were simply unreachable. Cantonese is deliberately left out: its
  token exists only in the large-v3 tokenizer, so offering it would be a choice that silently fails
  on every other model.
- `WaveHeader.TryParse`, which reads the RIFF/WAVE header `PcmAudio.CreateWaveStream` writes, for
  audio arriving from somewhere else — a speech server replying with `response_format: wav`, where
  the header is the only place the sample rate is stated. It walks the chunk list rather than
  assuming the canonical 44-byte layout, since servers interpose `LIST` and `fact` chunks that a
  fixed offset would play as audio, and it ignores the `data` chunk's declared size, since a server
  streaming a synthesis writes `0` or `0xFFFFFFFF` there and the stream ending is what ends the
  audio. Encodings whose samples are not integers are refused rather than handed back as PCM.
  ([#7])
- `AudioCaptureOptions.RetainBuffer`, and an `AudioCaptureOptions.Streaming` preset that turns it
  off. A capture session started with it off emits `FrameCaptured` as before and keeps none of the
  audio, so `StopAsync` returns an empty buffer. `IAudioRecorder` was documented as returning the
  complete recording, which obliged every session to hold it: fine for hold-to-talk, where a press
  lasts seconds and the buffer is the product, but a microphone left open — a phone or a desktop
  acting as a room microphone — accrued about 115 MB an hour that nothing would read. The default
  is unchanged, so Bantz and any existing consumer keep the recording they expect. ([#5])

### Fixed

- **Triggers and the D-pad can be bound.** On the controller Steam presents, L2, R2 and the D-pad
  are absolute axes rather than buttons, and Bantz read only buttons — so the most natural
  push-to-talk input on a handheld could not be captured at all, which is what "no gamepad button
  binds" looked like from outside. A trigger counts as held past half its travel.
- **A keybind capture that found nothing no longer locks the others out.** Arming a capture and
  never pressing anything it could see left it armed for the rest of the session, and every later
  attempt was refused with a message about finishing a recording — which had nothing to do with it.
  An already-armed capture is now replaced rather than refused, and the message when Bantz genuinely
  cannot listen says so.
- **Taps work on a touchscreen.** The interface runtime ships as two packages, and only one of them
  had been upgraded: the window and input plumbing live in the shell package, which was still three
  versions behind, so the release that added touch was not in the build at all. Both move together
  now. The runtime delivers touch only through its SDL window —
  the one it prefers has no touch API at all — so on a Steam Deck nothing could be tapped while the
  same build answered a mouse normally. Bantz now asks for the window that can hear a finger when
  the machine has a touchscreen, which it reads from the kernel's own device list. Setting
  `CUPRIFACE_SDL_GL` or `CUPRIFACE_SOFTWARE` yourself still wins, and a machine without a
  touchscreen is left as it was.
- The recording circle held 18px more than the space it declared, so it painted over the transcript
  card below it. Nothing looked wrong, because the overlap fell in a gap.
- First-run setup says **Continue** when the download finishes. It had been leaving
  "Downloading..." over a model that was ready — clicking it continued, because only the label was
  stale. The last progress report arrives before the downloaded file is verified, and verifying a
  141 MiB model against its published hash takes about a second in which nothing reports anything;
  the window that keeps the screen painting had closed by then, so the final label never appeared.
  The screen now keeps painting for as long as a download is running, whatever it is doing.
- Settings and diagnostics rows draw the separator between them. They asked for a `border-bottom`,
  which the interface engine does not support and silently ignored, so the line had never appeared.
- The runtime cards on first-run setup no longer print their description over their button. The
  card was a fixed height sixteen pixels shorter than the content it held, so the GPU card's third
  line of text and the top of "Prefer GPU" occupied the same nine pixels. The description is two
  lines on both cards now, and the card admits the height of what it contains.
- The buttons in a model row sit inside it. They were sized by padding alone, which made them
  taller than the row holding them, so they hung over its edge; and the first-run list showed a
  fourth row sliced through the middle by the edge of its box, which reads as broken rather than as
  "there is more below" — the scrollbar's job.
- A stray `</div>` in the Models tab, left over from replacing the language list with a dropdown.
- Compatibility mode waits for the clipboard change to be advertised before pressing Ctrl+V. A
  remote session does not share the clipboard: the client tells the server the clipboard changed,
  and the remote application pastes whatever the server holds when the keystroke arrives. Pressing
  in the same instant as the write beat that across the wire, so the remote side pasted nothing —
  visible as a paste that worked whenever something was already on the clipboard, and did nothing
  at all from an empty one. There is now a short gap between the two, and `BANTZ_PASTE_SETTLE_MS`
  raises it for a session that needs longer without needing a new build.
- Compatibility mode's Ctrl+V now carries a scan code. Every synthetic keystroke Bantz sent named
  only a virtual key, leaving the scan code zero. Ordinary windows read the virtual key and were
  fine; a Remote Desktop session or a virtual-machine console forwards the *scan code* to the
  session and so received nothing at all — the transcript reached the clipboard and the paste
  simply never happened. This is also why typing directly, which has no scan code either, was
  unreliable in those places to begin with. Keys are now sent one at a time with both, since a
  modifier arriving in the same instant as the key it modifies is not reliably seen as held across
  a wire.
- A modifier still held from the hold-to-talk shortcut no longer joins the paste. The shortcut is
  itself a chord — Ctrl+Shift+Space by default — and the paste follows the moment it is released,
  so a key still physically down turned Ctrl+V into Ctrl+Shift+V, which pastes differently or not
  at all depending on the application. Anything still held is lifted first.
- Compatibility mode pastes into Remote Desktop again — the case it exists for. Bantz put the
  transcript on the clipboard, sent Ctrl+V and took the transcript back 250 ms later. `SendInput`
  only queues the keystrokes, so that quarter-second was a race against the target reading the
  clipboard: a local text box won it, and a Remote Desktop session, which fetches clipboard data
  across the wire when the remote application asks for it, did not. The transcript had been
  withdrawn before it could be read, and nothing was pasted. It now stays on the clipboard for up
  to 2.5 seconds. Anything copied during that time is left alone rather than overwritten by the
  restore, which the old code would do.

### Changed

- Choosing a speech model no longer downloads it. Clicking a model started fetching it
  immediately, which committed you to a few hundred megabytes before you could see what it offered
  — whether it is multilingual, and which language it can be set to. The rows now read **Select**
  and **Selected**, a separate **Download** sits beside any model without a copy on disk, and a
  selected model that has not been downloaded still arrives on its first transcription, as it
  always did.
- The language picker appears only when the chosen model can act on it. An English-only model used
  to be offered a full language dropdown alongside a note explaining that the dropdown would not
  work — a choice and a contradiction of it on the same screen. Now that model says "transcribes
  English only" and offers nothing to set, and the space goes to the model list.
- First-run setup says which models are multilingual. The model rows there carried no badge at all,
  so nothing on that screen explained why a language could be chosen or why it sometimes could not
  be. They now carry the same ENGLISH and MULTI badges as the Models tab, the list shows enough
  rows to include the one that is selected, and the language row follows the same rule as the
  Models tab.

- **Breaking, for anyone implementing `ITranscriptionEngine`.** `IsReady`, `InitializeAsync` and
  `GetDiagnostics` are abstract; they used to have default implementations. A default interface
  method absorbs a near miss: an engine declaring `Task InitializeAsync(...)` where the interface
  declares `ValueTask` compiled without a warning, satisfied the interface with the default, and was
  then ignored by every caller holding an `ITranscriptionEngine` — surfacing much later as progress
  reporting that does nothing. Requiring all four turns that silence into a compile error. Derive
  from the new `TranscriptionEngineBase` to keep the conveniences; existing engines that implement
  every member, `WhisperTranscriptionEngine` among them, are unaffected. ([#6])
- `IAudioRecorder.StopAsync` now states what both recorders already did: it does not return until
  no further `FrameCaptured` will be raised, so the last of the audio can be handed off without
  racing the backend.

[#5]: https://github.com/Wixely/Bantz/issues/5
[#6]: https://github.com/Wixely/Bantz/issues/6
[#7]: https://github.com/Wixely/Bantz/issues/7

## 0.4.0

### Added

- **Models tab** for choosing the speech model and language. Tiny, Base, Small, Medium and Large v3
  Turbo are offered, each multilingual and most with a faster English-only build. Choosing a model
  that is not downloaded fetches it; one that is not in use can be deleted.
- Transcription in languages other than English, using a multilingual model. Twenty languages are
  offered in a dropdown, along with letting Whisper detect the language itself. The language can be chosen at any
  time and is remembered; an English-only model transcribes English while it is selected and says
  so, and the chosen language applies again as soon as a multilingual model is.
- The model and language are also chosen on the first-run setup card, which starts on Base
  (English) and English, so the first download is the one you meant to make. Choosing a language an
  English-only model cannot honour says so there rather than after the first transcription.
- `--download-model <id>`, which downloads one model and exits, for scripted or offline setup.
- `--data-root <path>`, which keeps settings, models and runtime files somewhere of your choosing
  instead of the per-user folder. It stands in for the executable's folder as well, so a portable
  installation beside the executable cannot claim a run that asked for somewhere else.

### Changed

- The window opens at 700x780 rather than 1170x1300, which fits a laptop screen without being
  resized. The layout is unchanged; it is drawn smaller.
- The model and language are read for each transcription, so changing either applies to the next
  one without a restart.
- Diagnostics reports the language and model in use rather than a fixed "English (en)".

### Fixed

- Scrollbars can be dragged. Two things stopped them. Bantz rebuilt its whole window ten times a
  second, and a rebuild cancels whatever the pointer is dragging, so a grabbed thumb stopped moving
  within a tenth of a second. The window is now rebuilt when something actually changes it from
  outside a click — audio levels while recording, a download's progress, a global shortcut — which
  also spares an idle Bantz the work.
- Scrollbars are far easier to grab. The thumb the runtime paints is a few pixels wide at this
  window's scale and only grabs a press that lands almost exactly on it, so grabbing it was close to
  a coin toss. Every list now reserves a wider gutter and takes the press itself anywhere in it, at
  any height, while a press on a row still selects the row. Dragging also survives whatever else the
  window is doing, so it works during a download.

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

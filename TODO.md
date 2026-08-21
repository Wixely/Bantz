# To do

- [ ] Add an equalizer-style recording notification so it is obvious when Bantz is listening, including while the main window is hidden. Show it for button and shortcut recordings, animate it while audio is being captured, and remove it immediately when recording stops, is cancelled, or fails.
- [ ] Detect recordings containing only silence or no meaningful sound and discard them before sending audio to the transcription model.
- [ ] Add a link to the Bantz GitHub repository on the Settings About page.
- [x] Detect the current display's available work area at startup and clamp Bantz's window size so it never opens larger than the screen, including on devices such as the Steam Deck.
- [ ] Add single-instance protection so launching Bantz again focuses or restores the existing window instead of starting a second process that could conflict over audio, shortcuts, settings, or tray state.

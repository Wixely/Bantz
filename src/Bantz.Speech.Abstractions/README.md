# Bantz.Speech.Abstractions

App-neutral contracts for speech-to-text engines. Audio is passed as signed 16-bit,
16 kHz, mono PCM through `PcmAudio`; engines expose explicit initialization,
progress, readiness, diagnostics, and a forward-compatible result type.

This package has no Whisper or native-runtime dependency.

# Bantz.Speech.Whisper

Local English transcription backed by Whisper.net and whisper.cpp. The package
downloads a pinned CPU or Vulkan runtime, verifies the runtime package hash, and
stores the `base.en` model at a consumer-selected path.

Recording-duration and silence policies intentionally belong to the consuming
workflow, so always-listening consumers can segment audio themselves.

Whisper.net's native loader is process-wide. Create as many engines as needed with
the same runtime choice, but select either CPU or Vulkan once per process before
the first model is loaded.

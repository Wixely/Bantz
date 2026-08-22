# Bantz.Capture

Hold-to-record microphone capture for Windows and Linux, with live PCM frame
callbacks and a complete-buffer result. Output is signed 16-bit, 16 kHz, mono
PCM. Windows uses NAudio wave-in; Linux uses ALSA `arecord`.

`AudioSignalAnalyzer` provides the shared equalizer and meaningful-sound metrics.
The default device is used unless `AudioCaptureOptions.DeviceId` is supplied.

# Bantz.Capture

Hold-to-record microphone capture for Windows and Linux, with live PCM frame
callbacks and a complete-buffer result. Output is signed 16-bit, 16 kHz, mono
PCM. Windows uses NAudio wave-in; Linux uses ALSA `arecord`.

`AudioSignalAnalyzer` provides the shared equalizer and meaningful-sound metrics.
The default device is used unless `AudioCaptureOptions.DeviceId` is supplied.

`AudioCaptureDevices.List()` enumerates the microphones available to record from,
always starting with `AudioCaptureDevices.Default`. Windows reports wave-in device
numbers as ids, Linux reports ALSA device names from `arecord -L`, and a platform
without a usable enumeration path reports the default device alone. Construct a
recorder with a `Func<AudioCaptureOptions>` to let a device chosen while the
application runs apply to the next recording.

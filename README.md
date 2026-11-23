# Soundprint Studio

[Download the Windows app](Soundprint-Studio-Windows.zip), extract the ZIP, and run `SoundprintStudio.exe`.

The app is portable and runs on Windows with .NET Framework 4.5 or newer.

1. Add or drag in WAV files, or add an M3U/M3U8 pointing to local audio.
2. Review the estimated tempo, key, rhythmic energy and brightness.
3. Choose the length, key and instrument palette. Click Generate track.
4. Play the result, click New variation, or save the stereo WAV.

MP3, FLAC, M4A and other compressed references require FFmpeg. If FFmpeg
is already on your PATH it is detected automatically. Otherwise place
ffmpeg.exe beside SoundprintStudio.exe. PCM WAV works on its own.

The seed recreates a track with the same settings and reference profile.
Changing references can change the result. Soundprint generates original
instrumentals with a procedural composer and synthesized instruments.
Standard streaming playlist links require local reference audio files.

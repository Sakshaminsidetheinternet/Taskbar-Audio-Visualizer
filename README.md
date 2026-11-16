# Taskbar Audio Visualizer

A lightweight Windows 11 audio spectrum visualizer. It captures the default playback device through WASAPI loopback and displays a soft-grey, 40-band equalizer in a transparent, click-through overlay aligned to the Windows taskbar.

> **Status:** Early development. The source is provided for building and testing; CPU, memory, battery use, DPI behaviour and recovery paths have not yet been benchmarked across hardware.

## Features

- System-wide playback capture (Spotify, browsers, games, etc.) through NAudio/WASAPI loopback.
- 40 logarithmically spaced frequency bands with a 2048-sample FFT.
- Soft-grey monochrome bars (`#999999`) with smooth attack/release.
- Transparent, borderless, click-through overlay; does not modify Explorer or Windows system files.
- Event-driven audio capture and throttled spectrum analysis.
- Rendering starts when audio is detected and stops after the bars decay.
- System-tray menu to show, hide, and exit. Hiding the visualizer also stops audio capture.

## Requirements

- Windows 11 (initial target: primary display, standard bottom taskbar)
- .NET 10 SDK
- Internet access on first build to restore the NAudio package

## Build and run

```powershell
git clone https://github.com/Sakshaminsidetheinternet/Taskbar-Audio-Visualizer.git
cd Taskbar-Audio-Visualizer\TaskbarAudioVisualizer
dotnet restore
dotnet run
```

Play audio through your default Windows output device. Use the tray icon to hide/show the overlay or exit the application.

## Publish a self-contained build

For 64-bit Windows:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The output is under `bin/Release/net10.0-windows/win-x64/publish/`.

## Power-conscious design

The app avoids a permanent 60 FPS animation loop. Audio arrives through WASAPI callbacks; FFT analysis is rate-limited, and the WPF render timer is only active while the bars are moving. When hidden, capture is stopped. These choices are intended to reduce background work, but actual power consumption depends on the audio driver, display, Windows compositor and hardware. No fixed CPU/RAM/battery figures are promised until measured.

## Limitations

- The first version targets the primary display and the normal bottom taskbar.
- It overlays the taskbar using a separate transparent window; it does not draw inside or patch the taskbar itself.
- Auto-hide taskbars, vertical taskbars, multi-monitor placement, per-monitor DPI transitions and Explorer restart recovery are not yet fully implemented.
- Audio capture uses the current default playback endpoint. Device changes may require restarting the app.
- Some drivers may not provide a usable shared-mode loopback stream.

## Troubleshooting

- If the bars remain still, confirm Windows is playing audio through the default output device and restart the app.
- If the overlay is misplaced, use a standard bottom taskbar on the primary display and check Windows display scaling.
- If the tray icon remains after an unexpected crash, exit the stale process from Task Manager.

## Roadmap

- Robust taskbar geometry updates and Explorer restart recovery.
- Multi-monitor and per-monitor DPI support.
- Automatic audio endpoint reconnection and sleep/resume recovery.
- Settings UI for bar count, sensitivity, height and colour.
- Automated build validation and measured performance benchmarks.

## License

MIT. See [LICENSE](LICENSE).

# Precision Jump

Precision Jump is a lightweight Windows 11 tray utility for continuous,
zoomable pointer navigation across multiple displays. It applies its mapping
only while active and does not change Windows mouse settings.

## How it works

1. Hold the activation shortcut (backtick by default).
2. Move the mouse normally. Physical movement is continuously mapped onto the
   connected display topology.
3. Scroll up to zoom in for precise movement.
4. Scroll down to zoom out for fast, accelerated travel.
5. Release the shortcut to keep the pointer position.

At the coarsest scale, displays are connected through a simplified
eight-direction topology. Zoomed movement uses their real Windows positions,
sizes, alignment, and gaps. Scaling is animated and never moves the pointer by
itself.

The overlay visualizes the current map scale with fixed grid lines and
distance-based fading. Right-click cancels and restores the activation
position.

## Features

- Continuous movement without cell-center jumps, thresholds, or cooldowns
- Zoom-based pointer acceleration and precision
- Eight-direction display topology at the coarsest scale
- Actual Windows display geometry while zoomed
- One-to-three-key activation sequences, including held-key combinations
- Configurable map response, maximum zoom depth, and status HUD
- Optional mouse movement and click recording to CSV
- Optional automatic startup with Windows
- Lightweight tray interface with no virtual cursor

## Optional event recording

Enable **Record mouse movement and clicks to CSV** in Settings to append mouse
events to:

```text
%LOCALAPPDATA%\MouseAccelerator\mouse-events-<session UTC>.csv
```

Each application session receives a new UTC-timestamped file opened with
`CreateNew`, so an existing recording is never appended to or overwritten.
The file has seven columns:

```text
t_us,x,y,evt,jumping,raw_dx,raw_dy
```

`jumping` is `true` while the continuous map is active. Recording runs through
a bounded background queue so disk writes do not block the input hook.
`t_us` is the high-resolution elapsed time since session start. `x/y` are the
effective pointer coordinates, while `raw_dx/raw_dy` capture the input
displacement. The writer flushes at least every 250 ms or 256 rows, including
during uninterrupted movement.

Enable **Start with Windows** to launch Precision Jump directly into the tray
after sign-in.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build -c Release --configfile .\NuGet.Config
dotnet publish -c Release --no-restore -o publish
```

Run `publish\MouseAccelerator.exe`. Settings are stored in:

```text
%LOCALAPPDATA%\MouseAccelerator\settings.json
```

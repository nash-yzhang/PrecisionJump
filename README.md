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
- Lightweight tray interface with no virtual cursor

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

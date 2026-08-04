# Precision Jump

Precision Jump is a lightweight Windows 11 tray utility for fast, precise
mouse positioning with a fixed screen grid and pointer-focused visibility. It
moves the real Windows pointer without changing mouse speed or acceleration.

## How it works

1. Hold the activation shortcut (backtick by default).
2. At the screen level, make a small directional mouse gesture to select an
   adjacent display.
3. Scroll up to open a screen-anchored 3×3 grid.
4. Gesture in one of eight directions to move by one grid step.
5. Scroll up for smaller steps; scroll down for larger steps.
6. Release the shortcut to keep the pointer position.

Grid lines remain fixed while the pointer moves between cells. Nearby cells
become clearer and more distant cells fade smoothly. There are no parent or
child regions, and scrolling changes only the grid density—it never moves the
pointer.

Right-click cancels the operation and restores the original pointer position.

## Features

- Angle-aware multi-monitor selection that favors the nearest aligned display
- Balanced eight-direction gestures with a radial dead zone
- Fixed screen grid with pointer-distance-based fading
- One-to-three-key activation sequences, including held-key combinations
- Configurable gesture distance, cooldown, sequence timeout, and maximum depth
- Lightweight tray interface with no virtual cursor or nested grid state

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

# Precision Jump

Precision Jump is a lightweight Windows 11 tray utility for fast, precise
mouse positioning with recursive grids. It moves the real Windows pointer
without changing mouse speed or acceleration.

## How it works

1. Hold the activation shortcut (backtick by default).
2. At the screen level, make a small directional mouse gesture to select an
   adjacent display.
3. Scroll up to open a full-screen 3×3 grid.
4. Gesture in one of eight directions to select a cell.
5. Scroll up again for finer precision; scroll down to return to a coarser
   level.
6. Release the shortcut to keep the pointer position.

Refinement applies to the entire selected display. The overlay shows the
parent grid faintly and expands the child grid under the pointer, so movement
can cross parent regions without losing precision.

Right-click cancels the operation and restores the original pointer position.

## Features

- Multi-monitor screen selection
- Stable eight-direction and corner-cell gestures
- Full-screen recursive 3×3 refinement
- One-to-three-key activation sequences, including held-key combinations
- Configurable gesture distance, cooldown, sequence timeout, and maximum depth
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

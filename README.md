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

Pointer positions can also be stored in Vim-style registers. Press the save
prefix (`Ctrl`, `M` by default), followed by `A`–`Z` or `0`–`9`. Press the jump
prefix (`Ctrl`, `G` by default), followed by the same register, to return. Both
prefixes are configurable in Settings, and saved positions persist across app
restarts. If a saved display is no longer connected, the destination is
clamped to the nearest available display.

The display map uses only shared edges. Screens whose left/right edges touch
are horizontal neighbors, and screens whose top/bottom edges touch are vertical
neighbors. Gaps and diagonal proximity never create a shortcut that can skip
another screen. Once two screens are neighbors, their full logical edges are
aligned so resolution and offset differences do not create dead zones. Scaling
is animated and never moves the pointer by itself.

The overlay visualizes the current map scale with fixed grid lines and
distance-based fading. Right-click cancels and restores the activation
position.

## Features

- Continuous movement without cell-center jumps, thresholds, or cooldowns
- Zoom-based pointer acceleration and precision
- Four-direction display topology based on shared screen edges
- Consistent adjacent-screen movement at every zoom level
- One-to-three-key activation sequences, including held-key combinations
- Persistent A–Z and 0–9 pointer-position registers with configurable prefixes
- Configurable map response, maximum zoom depth, and status HUD
- Optional mouse movement and click recording to CSV
- Lightweight tray interface with no virtual cursor

Low-level keyboard and mouse hooks run on a dedicated message thread. Hook
callbacks never render UI, write files, or call `SendInput`; mouse injection
and UI updates are handed off to separate workers, and callback failures reset
the active gesture so input fails open.

## Optional event recording

Enable **Record mouse movement and clicks to CSV** in Settings to append mouse
events to:

```text
%LOCALAPPDATA%\PrecisionJump\mouse-events-<session UTC>.csv
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

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build -c Release --configfile .\NuGet.Config
dotnet publish -c Release --no-restore -o publish
```

Run `publish\PrecisionJump.exe`. Settings are stored in:

```text
%LOCALAPPDATA%\PrecisionJump\settings.json
```

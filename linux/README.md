# Precision Jump — Linux/X11 port

A working Linux port of the Windows app in the parent directory. Same idea,
same mapping math, native X11 input.

Hold a key, move the mouse, and your physical movement is remapped onto a
**zoomable model of your monitor layout** instead of moving 1:1. Zoomed out,
a flick crosses every screen; zoomed in, the same flick moves a few pixels.

## Quick start

```bash
./run.sh
```

Then **hold `` ` ``** (backtick) and move the mouse.

| Input | Effect |
| --- | --- |
| Hold `` ` `` | Activate. The pointer starts mapping onto the display topology. |
| Move mouse | Travel the map. At the coarsest scale ~26px of real movement crosses a whole screen. |
| Scroll up | Zoom in — each step multiplies precision by 3. |
| Scroll down | Zoom out — each step multiplies reach by 3. |
| Release `` ` `` | Keep the pointer where it landed. |
| Right-click | Cancel and snap back to where you started. |

While the key is held the mouse belongs to Precision Jump: clicks and scrolls
do not reach the app underneath.

## Requirements

- **X11** (this port does not support Wayland — see Limitations)
- Python 3.9+ with GTK 3 bindings and cairo — on Fedora these are already
  present as `python3-gobject` and `python3-cairo`
- `python-xlib`:

```bash
python3 -m pip install --user python-xlib
```

Optional: `AppIndicator3` for the tray icon. On GNOME you also need the
*AppIndicator and KStatusNotifierItem Support* shell extension, otherwise the
app still runs fine — just without a tray icon (`--no-tray`).

## Options

```
./run.sh --no-tray                 run without the tray icon
./run.sh --no-overlay              disable the grid overlay
./run.sh --record                  record mouse events to CSV
./run.sh --key grave               set the activation key
./run.sh --key Control_L,k         a two-key activation sequence
```

Keys are named by **X keysym**, not by the character printed on the cap. Run
`xev` and press a key to find its name, or use `xmodmap -pke | grep -i <char>`.

This matters on non-US layouts. On the Spanish layout in use here, keycode 49
(the key left of `1`) carries `masculine` / `ordfeminine` / `grave` / `notsign`
across its four levels — so the default `grave` binding resolves to **the º key
left of `1`**, and pressing it plainly activates, because the grab covers every
modifier state. That is the same physical key as backtick on a US keyboard,
which is what upstream intends.

## Configuration

`~/.config/precision-jump/settings.json`

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Master switch. |
| `screen_jump_sequence` | `["grave"]` | 1–3 keysym names to activate. |
| `sequence_timeout_seconds` | `0.42` | Max gap between keys of a sequence. |
| `selection_distance` | `26` | Pixels of real movement per map unit. **Lower = faster travel.** |
| `maximum_zoom_level` | `8` | Zoom depth cap (scale is `3**depth`). |
| `show_overlay` | `true` | Draw the grid overlay. |
| `show_status_hud` | `true` | Show the KEEP / CANCELLED chip. |
| `record_mouse_events` | `false` | Write the CSV log. |
| `start_with_session` | `false` | Writes `~/.config/autostart/precision-jump.desktop`. |

## Event recording

With recording on, each session appends to a new file:

```
~/.local/share/precision-jump/mouse-events-<session UTC>.csv
t_us,x,y,evt,jumping,raw_dx,raw_dy
```

`t_us` is elapsed microseconds since session start, `x`/`y` are the effective
pointer coordinates, `raw_dx`/`raw_dy` the input displacement, and `jumping` is
`true` while the map is active. Files are opened exclusively, so an existing
recording is never overwritten. Writes go through a bounded background queue
that flushes every 250 ms or 256 rows, so disk I/O never stalls input.

## Tests

```bash
python3 tests/test_logic.py         # pure mapping math, no X server needed
python3 tests/test_engine_x11.py    # drives the real engine via XTest
python3 tests/test_overlay_visual.py /tmp/shots   # screenshots the overlay
```

The X11 tests briefly move your real cursor and restore it afterwards. All
grabs are released on exit, and the X server drops them automatically if the
process dies, so a crash cannot leave your desktop stuck.

## How the Windows version maps onto X11

| Windows | X11 |
| --- | --- |
| `SetWindowsHookEx(WH_KEYBOARD_LL)` | Passive `XGrabKey` in `GrabModeSync`. `AllowEvents(AsyncKeyboard)` swallows a key; `ReplayKeyboard` passes it through — the direct analogue of returning `1` vs `CallNextHookEx`. |
| `SetWindowsHookEx(WH_MOUSE_LL)` | `XGrabPointer` for the duration of the gesture, so motion, scroll and clicks reach us only. |
| `SendInput(MOUSEEVENTF_ABSOLUTE)` | `XWarpPointer`. |
| `LLMHF_INJECTED` check | Not needed. The delta is measured against the session's own position, so a warp echoes back as a zero delta and is ignored — which is what the original does too. |
| `System.Windows.Forms.Screen` | XRandR monitors. |
| WPF `AllowsTransparency` + `WS_EX_TRANSPARENT` | GTK window with an RGBA visual and pass-through input. |
| Registry `Run` key | `~/.config/autostart/*.desktop`. |
| `%LOCALAPPDATA%` | `$XDG_CONFIG_HOME` / `$XDG_DATA_HOME`. |

`Services/ScreenTargeting.cs` — the actual mapping algorithm — is ported
essentially line for line in `precision_jump/screen_targeting.py`, including
the eight-direction octant topology, the ray/rectangle slab clip used to cross
real layout gaps, the smoothstep scale easing, and C#'s away-from-zero rounding.

## Limitations

- **X11 only.** Wayland forbids a client from warping the pointer or grabbing
  global input, so this approach cannot work there. A Wayland version would
  need to read `/dev/input` via evdev and inject through `uinput`, which means
  adding your user to the `input` group and a compositor-specific overlay
  (`wlr-layer-shell`, unavailable on GNOME).
- At the coarsest zoom the pointer can be driven to the edge of the virtual
  desktop, where the physical mouse has no room left to generate motion. Zoom
  in a step, or lower `selection_distance`. The Windows build behaves the same
  way, since both warp the visible cursor to the mapped position.
- Auto-repeat is filtered by the standard timestamp-pair heuristic, so a held
  activation key is not mistaken for a release.
- **Only one instance can run at a time.** The activation key is grabbed
  exclusively, so a second instance starts normally but silently never
  activates. If nothing happens when you hold the key, check for a stray
  process with `pgrep -af precision_jump.app`.
- **The activation key is consumed while the app runs**, so with the default
  binding you can no longer type `` ` ``. This is upstream behaviour
  (`ShortcutPolicy.ShouldSuppressActivationKey`), not a porting artefact. Pick
  a key you don't type — `F13`, `Menu`, or `Scroll_Lock` are good choices:
  `./run.sh --key Menu`. Modifier keys are deliberately never swallowed.

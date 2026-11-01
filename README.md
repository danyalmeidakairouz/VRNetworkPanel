# VRNetworkPanel

A BepInEx 6 (IL2CPP) client mod for V Rising that draws a thin Dota 2 style readout of your
connection:

```
FPS: 144    PING: 38 ms    JITTER: 1 ms    LOSS IN: 0.0%    OUT: 0.0%
IN: 12.4 KB/s 60 pkt/s    OUT: 3.1 KB/s 60 pkt/s          (optional second line)
```

It is **read-only**: it reads the Steam connection's own real-time status and writes nothing to
the game.

## Install

1. Install BepInEx 6 for V Rising (IL2CPP build).
2. Drop `VRNetworkPanel.dll` into `BepInEx/plugins/` (or `BepInEx/BloodpebblePlugins/` if you use
   Bloodpebble - it supports hot reload; not both).
3. Launch the game. The readout shows in the top-left corner as soon as the mod loads. Press
   **F1** for the settings panel.

## Settings panel (F1)

Keyboard only - the game does not pass mouse input to mod UI.

| Key | Does |
|---|---|
| Up / Down | Select a setting |
| Left / Right | Switch OFF / ON, step a number, cycle the corner |
| Enter | Flip a switch; on a key setting, wait for the key to bind (Backspace unbinds, except for `MenuKey`; Esc cancels) |
| Delete | Restore the default |
| Esc | Close |

Every change is saved to the config file immediately and takes effect at once.

## What the numbers mean

| Value | Source | Yellow / red at |
|---|---|---|
| `FPS` | frames counted over half a second | below 50 / 30 |
| `PING` | Steam's round-trip estimate, ms | 80 / 150 ms |
| `JITTER` | mean size of each ping change over the last 5 s | 10 / 30 ms |
| `LOSS IN` | share of the server's packets that did not reach you | 1 % / 5 % |
| `OUT` | share of your packets that did not reach the server | 1 % / 5 % |

- **Loss needs the Steam transport.** Steam reports delivery quality per connection; the other
  transports in the game (Lidgren, EOS) do not. On those, loss shows `-` and ping falls back to the
  game's own latency figure, drawn grey with a `?` (e.g. `38 ms?`) because its unit is not yet
  confirmed.
- **Jitter is a floor.** Steam's ping is already a smoothed average, so jitter here is the
  steadiness of that average, not of individual packets: it reads lower than true packet jitter
  and cannot see packets arriving in bursts. A single route change lifts it for up to 5 s.
  `-` for the first 2 s after connecting, `0` if ping has not moved.
- Out-of-order packets count as not delivered, by Steam's definition.

## Settings

Edited from the F1 panel, or by hand in `BepInEx/config/vrnetworkpanel.cfg` (created on first
launch; hand edits apply on the next launch).

| Setting | Default | |
|---|---|---|
| `Enabled` | `true` | Show the readout |
| `MenuKey` | `F1` | Opens / closes the settings panel. Empty disables it |
| `ToggleKey` | *(empty)* | Key that shows / hides the readout and saves the choice. Unbound by default so it cannot clash with another mod |
| `Corner` | `TopLeft` | `TopLeft`, `TopCenter`, `TopRight`, `BottomLeft`, `BottomCenter` or `BottomRight` |
| `FontSize` | `13` | 8-40, shrunk further if the strip would not fit the screen |
| `SampleMs` | `500` | How often the shown numbers change, 100-5000. The connection is read ~10x a second regardless |
| `ShowTraffic` | `false` | Second line with KB/s and packets/s in and out (Steam only) |
| `Background` | `true` | Translucent strip behind the text |

Keys are single keys only (`F1`-`F24`, `A`-`Z`, `0`-`9`, `Space`, `Insert`, `Numpad0`-`9`, ...).
Hotkeys are ignored while the game window is not focused.

## Build

There is no solution file - build the project directly. Pass your install path:

```powershell
cd VRNetworkPanel
dotnet build -c Release -p:VRisingDir="D:\Steam\steamapps\common\VRising"
```

A successful build copies the DLL into `BepInEx/plugins/`. Add `-p:CopyToPlugins=false` to build
without deploying (needed while the game is running, since Windows locks the loaded DLL).

## Status

Runs in game on a Steam-transport server: ping, loss and traffic read live. The fallback for
non-Steam transports (ping from the game's own latency figure, shown with a `?`) has not been
seen on a real server yet. The log says which source the readout is using each time it changes.

## License

GPL-3.0, see `LICENSE`.

# FPS Unlocker

A [SMAPI](https://smapi.io/) mod for **Stardew Valley** that renders above 60 FPS while the game logic keeps its normal 60 ticks per second, so the game speed, timers and multiplayer sync stay vanilla.

- **More frames:** drawing is decoupled from the 60 Hz update loop. With vsync on it follows your monitor's refresh rate (for example 144 or 180 Hz).
- **Smooth motion:** on the frames drawn between two ticks, the camera, characters, projectiles, debris, light sources and rain are blended between their last two positions, so the extra frames show movement instead of repeating the same picture.
- **Safe by design:** positions are blended only for drawing and put back right after, without touching the game's network fields. Anything that moves more than a normal step in one tick (a warp, a respawned sprite) is drawn without blending.
- **Fails safe:** if a game update renames the internals the mod hooks into, it stays on vanilla behaviour instead of crashing.

The trade-off is up to one tick (16.7 ms) of visual delay, the usual cost of fixed-step interpolation.

## Requirements

- Stardew Valley 1.6 (developed on 1.6.15, Windows)
- [SMAPI](https://smapi.io/) 4.0 or later (developed on 4.5.1)

## Install

1. Install SMAPI following the guide on [smapi.io](https://smapi.io/) and start the game once through SMAPI.
2. Download `FpsUnlocker-<version>.zip` from [Releases](../../releases) and extract it into the game's `Mods` folder, so you end up with `Mods/FpsUnlocker/FpsUnlocker.dll` and `Mods/FpsUnlocker/manifest.json`.
3. Start the game through SMAPI.

For the most frames, turn vsync on in the game's options (it caps at the monitor's refresh rate) or set `MaxFps` below.

## Configuration

`Mods/FpsUnlocker/config.json` is created on the first start:

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `true` | Decouples rendering from the update loop. `false` = vanilla. |
| `MaxFps` | `0` | Render cap in frames per second. `0` = no cap (vsync, if on, still limits it). |
| `Interpolate` | `true` | Blends motion on the in-between frames. Without it the extra frames are copies. |
| `ShowFpsCounter` | `false` | Shows an FPS counter on screen. |

## Uninstall

Delete the `Mods/FpsUnlocker` folder.

## Build

The build expects the game at the path in `FpsUnlocker.csproj` (`GameDir`); pass `-p:GameDir=...` to use a different one.

```sh
dotnet build FpsUnlocker.csproj -c Release                 # build
dotnet build FpsUnlocker.csproj -c Release -p:Deploy=true  # build and copy into Mods/FpsUnlocker
```

## License

[MIT](LICENSE)

# AutoStage [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Automatic staging for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency).

Activates the next sequence whenever active engines run out of propellant. Works during auto-burns (continues the burn instead of aborting) and manual burns.

This mod is written against the [StarMap loader](https://github.com/StarMapLoader/StarMap).

Validated against KSA build version 2026.4.15.4141.

## Features

- **AUTOSTAGE toggle button** on the BurnControl gauge panel
- **Auto-burn continuation** - maintains BurnMode=Auto through staging so planned burns don't abort
- **Cascade staging** - stages again if the next stage is also empty
- **Ignition delay** - configurable delay between decoupler separation and engine ignition, simulating realistic engine spool-up time. Decouplers fire immediately, engines ignite after the configured delay.

## Ignition Delay

After staging, decouplers fire immediately but engines wait a configurable delay before igniting.

### Configuration

**Settings window (Settings > Mods > AutoStage Settings):** Configure the ignition delay per engine variant. All known engine variants are listed with an input field for the delay in seconds. Click "Save" to persist changes.

**Part Window (right-click engine > Window):** Override the delay for a specific sequence on the current vehicle. This per-vehicle override takes priority over the global engine config.

### Config files

Global config is stored in `Documents\My Games\Kitten Space Agency\mods\AutoStage\autostage.toml`:

```toml
[engine_delays]
CorePropulsionA_Prefab_EngineA2 = 2.0
CorePropulsionA_Prefab_EngineA3 = 5.0
```

Per-vehicle sequence overrides are stored in `Documents\My Games\Kitten Space Agency\mods\AutoStage\vehicles\<vehicle-id>.toml`. These files are created automatically when you set an override in the Part Window.

Removing the mod does not affect / corrupt game saves.

## Installation

1. Install [StarMap](https://github.com/StarMapLoader/StarMap) and [KittenExtensions](https://github.com/tsholmes/KittenExtensions).
2. Download the latest release from the [Releases](https://github.com/Maximilian-Nesslauer/KSA-AutoStage/releases) tab.
3. Extract into `Documents\My Games\Kitten Space Agency\mods\AutoStage\`.
4. The game auto-discovers new mods and prompts you to enable them. Alternatively, add to `Documents\My Games\Kitten Space Agency\manifest.toml`:

```toml
[[mods]]
id = "AutoStage"
enabled = true
```

## Dependencies

- [StarMap.API](https://github.com/StarMapLoader/StarMap) (NuGet)
- [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) (NuGet)
- [KittenExtensions](https://github.com/tsholmes/KittenExtensions) (for XML patching)

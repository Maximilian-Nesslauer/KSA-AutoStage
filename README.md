# AutoStage [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

Automatic staging for [Kitten Space Agency](https://ahwoo.com/app/100000/kitten-space-agency).

Activates the next sequence whenever active engines run out of propellant. Works during auto-burns (continues the burn instead of aborting) and manual burns.

<table>
  <tr>
    <th align="center">Stock</th>
    <th align="center">With AutoStage</th>
  </tr>
  <tr valign="top">
    <td><img src="images/stock.png" alt="Stock BurnControl panel" width="420" /></td>
    <td><img src="images/autostage.png" alt="BurnControl panel with AUTOSTAGE toggle" width="420" /></td>
  </tr>
</table>

This mod is written against the [StarMap loader](https://github.com/StarMapLoader/StarMap).

Validated against KSA build version 2026.4.16.4170.

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

| Package | Purpose | Tested version |
| --- | --- | --- |
| [StarMap](https://github.com/StarMapLoader/StarMap) | Mod loader, required at runtime (see [Installation](#installation)) | 0.4.5 |
| [KittenExtensions](https://github.com/tsholmes/KittenExtensions) | Required at runtime for XML patching | v0.4.0 |

## Build dependencies

Required only to build the mod from source. Targets **.NET 10**.

| Package | Source | Tested Version |
| --- | --- | --- |
| [StarMap.API](https://github.com/StarMapLoader/StarMap) | NuGet | 0.3.6 |
| [Lib.Harmony](https://www.nuget.org/packages/Lib.Harmony) | NuGet | 2.4.2 |

## Mod compatibility

- Known conflicts: none

## Community

Thread on the KSA forums: https://forums.ahwoo.com/threads/autostage.891/

## Check out my other mods

- [StageInfo](https://github.com/Maximilian-Nesslauer/KSA-StageInfo) - per-stage dV, TWR and burn time readouts in the stage info panel ([forum thread](https://forums.ahwoo.com/threads/stageinfo.905/))
- [AdvancedFlightComputer](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer) - set periapsis / set apoapsis / match or set inclination quick-tools in the Transfer Planner, plus hyperbolic-target support (Oumuamua, 2I/Borisov, 3I/ATLAS)

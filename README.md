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

Validated against KSA build version 2026.7.8.4980.

## Features

- **AUTOSTAGE toggle button** on the BurnControl gauge panel
- **Auto-burn continuation** - maintains BurnMode=Auto through staging so planned burns don't abort
- **Cascade staging** - stages again if the next stage is empty or only has decouplers
- **Configurable staging delays** - independent delays for decouplers and engines, simulating realistic separation and engine spool-up time
- **On-screen countdowns** - "Decouple in X.Xs" and "Ignition in X.Xs" alerts during a delayed stage so you can see what's about to happen

## Staging Delays

Two delays are configurable per part variant, both measured from the staging trigger:

- **Engine ignition delay** - default values per stock engine variant (the small EngineA1 ignites after 2 s, EngineA3 after 3 s, etc.)
- **Decoupler delay** - default 0 s (fires immediately, matches stock behaviour)

Set the decoupler delay shorter than the engine delay if you want the lower stage to drop away before the upper stage lights up.

### Configuration

**Settings window (Settings > Mods > AutoStage Settings):** Two sections, "Engine Ignition Delays" and "Decoupler Delays". All known part variants are listed with an input field for the delay in seconds. Click "Save" to persist changes.

**Part Window (right-click part > Window):** Override the delay for a specific sequence on the current vehicle. Engines show "Ignition Delay", decouplers show "Decoupler Delay". Per-vehicle overrides take priority over the global config.

### Config files

Global config is stored in `Documents\My Games\Kitten Space Agency\mods\AutoStage\autostage.toml`:

```toml
[engine_delays]
CorePropulsionA_Prefab_EngineA2 = 2.0
CorePropulsionA_Prefab_EngineA3 = 5.0

[decoupler_delays]
CoreFairingA_Prefab_Interstage3W3HB = 1.0
```

Per-vehicle sequence overrides are stored in `Documents\My Games\Kitten Space Agency\mods\AutoStage\vehicles\<vehicle-id>.toml`. These files are created automatically when you set an override in the Part Window. They have separate `[sequence_delays]` (engines) and `[decoupler_delays]` sections.

Removing the mod does not affect / corrupt game saves.

## Installation

1. Install [StarMap](https://github.com/StarMapLoader/StarMap) and [KittenExtensions](https://github.com/tsholmes/KittenExtensions).
2. Download the latest release from the [GitHub Releases](https://github.com/Maximilian-Nesslauer/KSA-AutoStage/releases) tab or from [SpaceDock](https://spacedock.info/mod/4254/AutoStage).
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

## Testing

`AutoStage.HarnessTests/` is a developer-only test suite for [HeadlessHarness](https://github.com/Maximilian-Nesslauer/KSA-HeadlessHarness), which brings the real game up GPU-free and runs plug-in tests against the live simulation:

- `autostage-api-drift` boots AutoStage's actual load path and checks every reflection target and the gauge enum injection against the current game build, so an update that breaks the mod is caught without flying anything.
- `autostage-flight` flies a staged save at full manual throttle and asserts that AutoStage performs every further staging on its own.
- `autostage-delays` measures that configured decoupler and engine ignition delays fire on time.

To run it: build this solution and the HeadlessHarness repo, checked out as a sibling of this one (their `CopyToMods` targets deploy everything), then run the harness's `scripts/run-headless.ps1` (optionally with a `-Tests` name filter). The flying tests use the save named by `KSA_HEADLESS_VEHICLE` and skip when it is unset. Leave the deployed test mod disabled for normal play; it only does anything inside a harness run and is not part of the released mod.

## Mod compatibility

- Known conflicts: none

## Community

Thread on the KSA forums: https://forums.ahwoo.com/threads/autostage.891/

## Check out my other mods

- [AdvancedFlightComputer](https://github.com/Maximilian-Nesslauer/KSA-AdvancedFlightComputer) - Transfer Planner quick-tools (set Pe/Ap, match/set inclination, circularize), multi-pass burn splitting, and hyperbolic-target support (Oumuamua, 2I/Borisov, 3I/ATLAS) ([forum thread](https://forums.ahwoo.com/threads/advanced-flight-computer.783/))
- [AutoRemoveFinishedBurns](https://github.com/Maximilian-Nesslauer/KSA-AutoRemoveFinishedBurns) - automatically removes finished auto-burns from the burn plan ([forum thread](https://forums.ahwoo.com/threads/autoremovefinishedburns.928/))
- [DeltaVMap](https://github.com/Maximilian-Nesslauer/KSA-DeltaVMap) - interactive delta-v subway map and transfer-window planner, auto-generated from the loaded system ([forum thread](https://forums.ahwoo.com/threads/deltavmap.978/))
- [MeasureTools](https://github.com/Maximilian-Nesslauer/KSA-MeasureTools) - click-to-measure ruler, protractor, and surface measuring in the map view ([forum thread](https://forums.ahwoo.com/threads/measuretools.992/))

# Nivalis Minimap

A minimap and a full district map for Nivalis Nights: a satellite view of where you are, with vendors, travel points, your apartment and venues, and your quest objectives.

## What you get

- **A round minimap** in the bottom-left corner. It turns with your view (a red dot on the rim marks north), shows about 60 metres around you, and hides whenever the game's HUD does: menus, dialogue, cutscenes, fast travel and loading screens. When the game's venue panel is showing (hours, name and rating, bottom left, at one of your venues), a bottom-left minimap slides up above it. **F6** hides and shows it.
- **A full district map** on **F7**: north up, scroll to zoom towards the cursor, drag to move, hover a badge for its name. **F7** or **Esc** closes it. (M stays the game's travel map.)
- **Badges:**
  - **Vendors:** ringed in a colour for the kind of shop (produce, butcher, fish, drinks, hardware, furniture...), with something they sell inside.
  - **Places:** travel points, trains, boats and lifts (hover shows where they go, e.g. "Train > Docks"), greenhouses, shelters and your apartment, in the compass's own icons.
  - **Venues:** yours, and dimmed ones you could buy or rent (hover shows the prices).
  - **Quests:** your pinned quests by default. A quest off the minimap's edge sits on the rim, pointing the way; one in another district sits on the exit that leads there.

## How the maps are made

Each district's map is made **on your machine** the first time you visit it: the game photographs the district from straight above (or, in the Drawn style, the walkable ground is drawn as a plain map). It's saved in `BepInEx\cache\NivalisMinimap\maps` and loaded from there after that. The mod contains no pictures of the game.

- **The first visit to each district** pauses the game for a few seconds while the map is made.
- **The map is a snapshot:** furniture you move, or a district first visited at night, shows as it was. Pause menu > **Mods** > Nivalis Minimap > **Redraw this district's map** makes it again.
- **A game update** redraws maps automatically on the next visit.

## Settings

Pause menu > **Mods** > Nivalis Minimap. Changes apply immediately. They're stored in `BepInEx\config\bgasm.nivalis.minimap.cfg`.

| Setting | Default | Does |
|---|---|---|
| `Enabled` | on | Show the minimap. |
| `Style` | Satellite | **Satellite**: the district from above. **Cut**: the same with roofs and awnings removed. **Drawn**: the walkable ground as a plain map. |
| `Corner` | BottomLeft | Screen corner. |
| `Size` | 260 | Diameter on a 1080p screen. |
| `Zoom` | 60 | Metres across the minimap. |
| `RotateWithCamera` | on | Turn the map with your view. Off: north is always up. |
| `Opacity` | 0.95 | How solid the minimap is. |
| `CutHeight` | 3 | Cut style: metres above the highest walkable ground where the view is sliced off. |
| `ToggleKey` | F6 | Hides and shows the minimap. Any [Unity Input System key name](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.0/api/UnityEngine.InputSystem.Key.html); empty for none. |
| `FullMapKey` | F7 | Opens the full map. |
| `ShowVendors` | on | Vendor badges. |
| `ShowPlaces` | on | Places, your apartment and venues. |
| `QuestMarkers` | Pinned | **Pinned** (quests you track), **All** (others dimmed), or **Off**. |

## Installation

1. Install BepInEx 6 bleeding edge (IL2CPP, build 788). See the [repository README](../../README.md#requirements).
2. Install [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) **0.4.0 or later**.
3. Extract this mod's zip into the game folder: `BepInEx\plugins\NivalisMinimap.dll`.

To uninstall, delete `NivalisMinimap.dll` (and `BepInEx\cache\NivalisMinimap` for the saved maps). Saves are unaffected.

## For mod authors

The minimap is built on the kit's world APIs, and is the example of using them: `Navigation.Triangulate`, `Photo.TopDown`, `Player.Heading`, `Ui.GameHudAlpha`, `World.Places`, `Economy.Stalls`, `Venues.Entrances` and `Quests.Markers`. See [MapSource.cs](MapSource.cs) (making a map), [Hud.cs](Hud.cs) (the minimap) and [Markers.cs](Markers.cs) (badges).

With the kit's dev console on, `minimap` lists the research tools: `minimap shot` (photograph the district with your own options), `minimap dump` (the navigation mesh drawn), `minimap lights`, `minimap renderers`, `minimap state`, `minimap refresh`.

## Credits

By BGASM. Built on [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) and BepInEx.

## Changes

**0.1.1**
- Moves up above the venue info panel instead of covering it.

**0.1.0**
- First release.

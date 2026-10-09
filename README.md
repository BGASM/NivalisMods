# Nivalis Nights Mods

BepInEx plugins for Nivalis Nights, by BGASM.

| Mod | What it does | Needs |
|---|---|---|
| [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) (own repository) | Shared library for other mods: events, settings in a pause-menu Mods browser, UI building blocks. Does nothing on its own. | |
| [Better Supplier Choice](mods/NivalisOrderFix) (formerly Manager Order Fix) | Choose the order managers buy from vendors: cheapest, nearest, or balanced. | Nivalis ModKit 0.6.2 |
| [Nivalis Minimap](mods/NivalisMinimap) | A satellite minimap and full district map with vendors, travel points, your venues and quests. Maps are made on your machine. | Nivalis ModKit 0.6.2 |
| [Nivalis Ledger](mods/NivalisLedger) | A live profit tracker for your venues in your browser: sales, plate costs and margins, stock and its value, pending orders, staff pay and reviews. | Nivalis ModKit 0.6.2 |
| [Nivalis Bartender](mods/NivalisBartender) | A Bartender job in the venue's Staff tab: bartenders prep and plate the drinks, cooks stick to food. Drinks can use cooking, serving, or both. | Nivalis ModKit 0.6.2 |
| [Nivalis Staff Order](mods/NivalisStaffOrder) | Up and down arrows on your venue's staff list, to put your staff in the order you like. Display only. | Nivalis ModKit 0.2 |
| [Nivalis Time Speed](mods/NivalisTimeSpeed) | Run the in-game clock at 0.5x, 1x or 2x. | Nivalis ModKit 0.2 |
| [Nivalis Cursor](mods/NivalisCursor) | A free mouse cursor on demand (scroll-wheel click by default): movement and mouse-look pause until you press it again. | Nivalis ModKit 0.6.2 |
| [Nivalis Borderless](mods/NivalisBorderless) | Borderless windowed mode at your monitor's resolution, which the game's settings don't offer. | |
| [Better Supplier Choice (Standalone)](mods/NivalisOrderFixStandalone) | The same vendor order without the kit. Install one edition, not both. | |
| [Use Oldest First](mods/NivalisFifo) | **Retired.** Game patch 2 made it unnecessary: the game now uses its least-fresh stock first. Kept for reference. | |

## Requirements

All mods need BepInEx 6 bleeding edge, Unity IL2CPP build.

Download: https://builds.bepinex.dev/projects/bepinex_be

Pick build 788 and download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`. Install steps are in each mod's README.

## Building

Requires the .NET SDK and a BepInEx install that has run the game once.

    dotnet build -c Release -p:GameDir="C:\path\to\Nivalis Nights"

Zips land in `releases/`. The projects in `samples/` are test harnesses and example code for the kit. They build into your plugins folder but are never packaged.

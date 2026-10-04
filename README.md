# Nivalis Nights Mods

BepInEx plugins for Nivalis Nights, by BGASM.

| Mod | What it does | Needs |
|---|---|---|
| [Nivalis ModKit](mods/NivalisModKit) | Shared library for other mods: events, settings in a pause-menu Mods browser, UI building blocks. Does nothing on its own. | |
| [Manager Order Fix](mods/NivalisOrderFix) | Choose the order managers buy from vendors: cheapest, nearest, or balanced. | Nivalis ModKit 0.2 |
| [Manager Order Fix (Standalone)](mods/NivalisOrderFixStandalone) | The same vendor order without the kit. Install one edition, not both. | |
| [Use Oldest First](mods/NivalisFifo) | **Retired.** Game patch 2 made it unnecessary: the game now uses its least-fresh stock first. Kept for reference. | |

## Requirements

All mods need BepInEx 6 bleeding edge, Unity IL2CPP build.

Download: https://builds.bepinex.dev/projects/bepinex_be

Pick build 788 and download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`. Install steps are in each mod's README.

## Building

Requires the .NET SDK and a BepInEx install that has run the game once.

    dotnet build -c Release -p:GameDir="C:\path\to\Nivalis Nights"

Zips land in `releases/`. The projects in `samples/` are test harnesses and example code for the kit. They build into your plugins folder but are never packaged.

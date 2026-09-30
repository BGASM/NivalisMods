# Nivalis Nights Mods

BepInEx plugins for Nivalis Nights, by BGASM.

| Mod | What it does | Needs |
|---|---|---|
| [Use Oldest First](mods/NivalisFifo) | Cooks use the oldest ingredients first. | |
| [Manager Order Fix](mods/NivalisOrderFix) | Managers buy what they need and stop. | Nivalis ModKit |
| [Nivalis ModKit](mods/NivalisModKit) | Shared library for other mods. Does nothing on its own. | |

## Requirements

All mods need BepInEx 6 bleeding edge, Unity IL2CPP build.

Download: https://builds.bepinex.dev/projects/bepinex_be

Pick build 788 and download `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`. Install steps are in each mod's README.

## Building

Requires the .NET SDK and a BepInEx install that has run the game once.

    dotnet build -c Release -p:GameDir="C:\path\to\Nivalis Nights"

Zips land in `releases/`. The projects in `samples/` are test harnesses and example code for the kit. They build into your plugins folder but are never packaged.

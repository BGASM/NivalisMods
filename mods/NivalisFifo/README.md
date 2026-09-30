# Use Oldest First

A Nivalis Nights mod. Cooks use the oldest ingredients first.

By default the game takes whatever you bought last. Aging stock sits under fresh stock until it rots. This mod reorders each ingredient stack before anything is removed, so the item closest to spoiling goes first. Spoiled items are never picked ahead of usable ones.

Also applies to transfers between inventories.

## Requirements

BepInEx 6 bleeding edge, Unity IL2CPP build.

Download: https://builds.bepinex.dev/projects/bepinex_be

On that page, pick build 788 and download this file:

`BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788+5b766a3.zip`

The name must contain `Unity.IL2CPP` and `win-x64`. These will not work:

- BepInEx 5.x (Mono only)
- BepInEx 6 `Unity.Mono` or `NET` builds
- BepInEx 6.0.0-pre.2 from GitHub (untested)

Newer bleeding edge builds will likely work. Only be.788 is tested.

## Install

1. In Steam, right-click Nivalis Nights, then Manage > Browse local files.
2. Extract the BepInEx zip into that folder. `winhttp.dll` and the `BepInEx` folder should sit next to the game's exe.
3. Launch the game once and wait for it to reach the menu. The first launch is slow.
4. Close the game. Extract this mod's zip into the same folder.

The mod is working if `BepInEx\LogOutput.log` contains `Use Oldest First loaded`.

## Uninstall

Delete `BepInEx\plugins\NivalisFifo.dll`. Saves are unaffected. The mod only changes list order and adds no save data.

## Building from source

Requires the .NET SDK and a BepInEx install that has run the game once.

```
dotnet build -c Release -p:GameDir="C:\path\to\Nivalis Nights"
```

## License

MIT
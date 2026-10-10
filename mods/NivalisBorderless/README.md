# Nivalis Borderless

Keeps the game on screen when you click another window. Since the October 9 patch the game's Display Mode has **Fullscreen Window** (borderless, at your monitor's resolution), but the game still minimises as soon as you click a browser or a second screen. With this mod it stays where it is.

Set the game's Display Mode to **Fullscreen Window**. In Windowed and Exclusive Fullscreen the mod does nothing and the game behaves as normal.

While you're in another window the game keeps running and stays visible, so you can watch it (or the [Nivalis Ledger](../NivalisLedger)) on a second screen.

## Settings

`Enabled` (on): turn the mod off without uninstalling. In Mod Settings Menu, or `BepInEx\config\bgasm.nivalis.borderless.cfg`.

## Installation

Needs BepInEx 6 (IL2CPP, be.788). No other mods needed. Extract the zip into the game folder: `BepInEx\plugins\NivalisBorderless.dll`.

## Changes

**1.0.1**
- For the October 9 patch, which added Fullscreen Window to the game's Display Mode. The mod now works with that setting instead of making its own borderless window: it only stops the game minimising when you click away.
- Fixes the game freezing at startup (or the log repeating "removing the border") on the patched game: 1.0's own window fought the game's new setting.

**1.0.0**
- First release.

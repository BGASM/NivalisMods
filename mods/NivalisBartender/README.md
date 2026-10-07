# Nivalis Bartender

In Nivalis Nights every drink is a cook's job. Beer is often half of all orders, so your cooks spend half their trips (and kitchen station time) pouring drinks while the food waits.

This mod adds a **Bartender** job to the venue's **Staff** tab, next to Cook, Waiter, Cleaner and Manager. It shows for anyone with cooking skill and uses their cooking level.

| Jobs on | Makes |
|---|---|
| **Bartender** | drinks only |
| **Cook + Bartender** | anything, in order (the game's usual cook) |
| **Cook** | food only; drinks too when no bartender is on shift |

Bartender counts as a job, so a dedicated bartender can have Cook switched off. The job is kept with your save.

**Removing the mod is safe:** your saves always keep bartenders as cooks underneath, so without the mod they simply work as cooks again (they never end up with no job).

## Settings

Pause menu > **Mods** > Nivalis Bartender, or `BepInEx\config\bgasm.nivalis.bartender.cfg`:

| Setting | Default | Does |
|---|---|---|
| `Enabled` | on | Route drinks to bartenders and food to the other cooks. |
| `CooksCoverDrinks` | on | Off: cooks never make drinks, even with no bartender on shift. |

## Installation

Needs BepInEx 6 (IL2CPP, be.788) and [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) 0.2.0 or later. Extract the zip into the game folder: `BepInEx\plugins\NivalisBartender.dll`.

# Nivalis Bartender

In Nivalis Nights every drink is a cook's job. Beer is often half of all orders, so your cooks spend half their trips (and kitchen station time) pouring drinks while the food waits.

This mod adds a **Bartender** job to the venue's **Staff** tab, next to Cook, Waiter, Cleaner and Manager. Bartenders prep and plate the drinks; cooks stick to food.

| Jobs on | Makes |
|---|---|
| **Bartender** | drinks only (prep and plating) |
| **Cook + Bartender** | anything, in order (the game's usual cook) |
| **Cook** | food only; drinks too when no bartender is on shift |

Bartender counts as a job, so a dedicated bartender can have Cook switched off. The job is kept with your save. Venues without a bartender, including every venue you don't own, run exactly as in the game.

## Which skills make the drinks

Bartending is part kitchen, part service. The `DrinkSkill` setting decides which of a worker's skills count, and who may tend bar:

| `DrinkSkill` | Who can be a bartender | Drink prep | Drink plating (quality, speed, XP) | Badge |
|---|---|---|---|---|
| **Mixed** (default) | staff with cooking **and** serving | cooking | serving | serving/cooking, e.g. `3/1` |
| **Cooking** | anyone who can cook | cooking | cooking (as in the game) | cooking level |
| **Serving** | anyone who can wait tables | serving | serving | serving level |

In the game a dish's quality comes from whoever plates it: their cooking level times how happy they are. With serving, the same happiness counts, times their serving level's service quality (the value a waiter's service adds to reviews). Meal quality counts toward review scores, so a skilled, happy bartender means better reviews from drinks.

Bartenders also earn XP for drink prep, which the game gives no one for prep: `DrinkPrepXp` per drink (0.5 by default), however many prep steps it has. Plating gives 1, as in the game. Beer has no prep; drinks like Galaxy Lemonade (blender) do.

A new `DrinkSkill` applies when you close the settings menu, so you can click through the options freely. Anyone who doesn't have the skills it needs loses the Bartender job then (and gets a game job if it was their only one).

## Settings

Pause menu > **Mods** > Nivalis Bartender, or `BepInEx\config\bgasm.nivalis.bartender.cfg`:

| Setting | Default | Does |
|---|---|---|
| `Enabled` | on | Bartenders make the drinks and cooks the food. |
| `DrinkSkill` | Mixed | Which skills make a bartender's drinks (see above). |
| `DrinkPrepXp` | 0.5 | XP for the prep of one drink, split across its prep steps (0 to 2). |
| `CooksCoverDrinks` | on | With no bartender on shift, cooks make the drinks. Off: cooks never make drinks at a venue with bartenders. |
| `CooksHelpDrinkPrep` | off | Cooks help with drink prep when there's no food prep for them, even with a bartender on shift. Plating drinks stays with bartenders. |
| `Verbose` | off | Log who takes which jobs (changes only) and each drink a bartender plates, with its quality and XP. |

## Removing the mod

Safe. A bartender with no other job is saved with a stand-in game job (Cook if they can cook, else Waiter), taken off again as soon as the save is written. Without the mod they load as working cooks or waiters, never with no job.

## Installation

Needs BepInEx 6 (IL2CPP, be.788) and [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) 0.6.0 or later. Extract the zip into the game folder: `BepInEx\plugins\NivalisBartender.dll`.

## For modders

The job, its save safety and the kitchen hooks come from the kit (`StaffJobs`, `Kitchen`, `StaffSkills`); this mod only makes the decisions. See the kit's [Staff and kitchen](https://github.com/BGASM/NivalisModKit#staff-and-kitchen-experimental-kit-060) section.

# Better Supplier Choice

*Formerly Manager Order Fix. Same mod, same settings: the old name described a bug the game has since fixed.*

Choose which vendors your managers buy ingredients from: the cheapest, the nearest, or a balance of price, distance and stock.

## The over-buy bug (fixed by the game)

Before the game's first patch (October 1, 2026), managers bought the full order from every vendor that stocked an ingredient. Needed 5 potatoes, bought 10. Needed 1 garlic, bought 4 from four shops. Version 1.x, as Manager Order Fix, existed to fix that.

The patch fixed it in the game: managers now keep a running total and stop when the order is filled. Better Supplier Choice no longer needs to correct quantities, and doesn't change them. On the patched game it only changes which vendors are used, and in what order.

## What the mod does

When a manager restocks an ingredient, the game walks its vendor list, most stock first, and buys until the order is filled. Better Supplier Choice lets the game work out how much to buy, then has the purchases made in the vendor order you choose. Managers still decide when to reorder and how much, and still buy fresh stock only.

If a purchase fails, usually because the venue is out of money, that recipe's purchases stop, the same point where the game's own loop stops.

The purchase handling lives in [Nivalis ModKit](../NivalisModKit), which this mod requires.

## Settings

Change them in game: pause menu > **Mods** > Better Supplier Choice. Changes apply immediately. They're stored in `BepInEx\config\bgasm.nivalis.orderfix.cfg`, created on first launch, which you can also edit directly. Settings from 1.x (`will.nivalis.orderfix.cfg`) are copied over automatically.

`VendorSort` picks the vendor order:

| Mode | Order |
|---|---|
| `Vanilla` | Most stock first, the game's own order. Default. Same as not having the mod. |
| `Cheapest` | Lowest price first. Most buying shifts to Calypso Island. |
| `Local` | Nearest district first. Your own district can be the most expensive. |
| `Balanced` | Weighs price, distance and stock. |

The other modes change how the city's economy moves: every managed venue in the city buys this way, NPC venues included.

`Balanced` scores each vendor as `price × (1 + DistanceWeight × hops) × (1 + ScarcityWeight / stock)` and buys from the lowest score.

| Setting | Default | Effect |
|---|---|---|
| `DistanceWeight` | 0.07 | Cost per district hop. Higher stays closer to home. Lower chases discounts. |
| `ScarcityWeight` | 2.0 | Steers away from shops running low. |
| `Verbose` | false | Logs every restock round: the vendors bought from, and each skipped vendor's price, stock and hops on one line. Under "Show advanced" in game. |

Hops are counted over the game's travel graph, `WorldLocation.transitions`. Your own district is 0.

## In game

Pause menu > **Mods** > **Better Supplier Choice** shows today's results for your venues:
- the current mode, which you can switch there
- how many ingredient orders the fix filled, and what they cost
- the difference from the game's own vendor choice (Cheapest and Balanced usually save money; Local can cost a little more, for nearer vendors)

The counts restart each game day.

For mod developers, the dev command `orderfix` (`orderfix mode=Cheapest` to switch) returns the same numbers. See the kit's dev console.

## Requirements

- BepInEx. See the [root README](../../README.md).
- [Nivalis ModKit](../NivalisModKit) 0.2 or later.

## Install

1. Install BepInEx. See the [root README](../../README.md).
2. Launch the game once. Wait for the main menu, then close the game.
3. Extract the Nivalis ModKit zip into the game folder.
4. Extract this mod's zip into the same folder. The file lands at `BepInEx\plugins\NivalisOrderFix.dll`.
5. Launch the game.

To confirm it loaded, open `BepInEx\LogOutput.log` and look for `Purchasing pipeline: live` from the kit, then `Better Supplier Choice loaded`.

If the kit is missing, BepInEx skips Better Supplier Choice and logs that a dependency is missing.

Upgrading from 1.x: install the kit, then replace `NivalisOrderFix.dll`. Your settings carry over.

To uninstall, delete `NivalisOrderFix.dll`. Remove the kit too if nothing else needs it. Saves are unaffected.

## Compatibility

Other mods that change how managers buy ingredients through the kit's `Purchasing` API work alongside it. Mods that hook `Vendor.BuyItem` directly will conflict.

Tested on Nivalis Nights 1.0 patch 2 (Steam build 25680465) with BepInEx be.788. Without the patch, the over-buy bug is still corrected.

## Changes

**2.1.0**
- Renamed from Manager Order Fix to Better Supplier Choice. The GUID, settings file and `NivalisOrderFix.dll` are unchanged, so updating keeps your settings.
- Settings can be changed in game through the kit's Mods menu (pause menu > Mods). Requires Nivalis ModKit 0.2.
- A page in the Mods menu shows today's results and the difference from the game's own vendor choice.
- Verbose logging is one line per round of skipped vendors instead of one per vendor.
- `DistanceWeight` is limited to 0–0.5 and `ScarcityWeight` to 0–10, so they show as sliders.

**2.0.0**
- Requires Nivalis ModKit. The purchase handling moved into the kit, so other mods can adjust vendor order or quantities without conflicting.
- Settings file renamed to `bgasm.nivalis.orderfix.cfg`. 1.x settings are copied over on first launch.
- Works with the game's first patch, which fixed the over-buy bug. On the patched game, Better Supplier Choice only sets the vendor order.

**1.0.0**
- First release.

## Technical notes

`BuyItem` takes its `ShopTradeRequest` and `BasicTemp` structs by reference. Harmony's IL2CPP trampoline mishandles by-reference structs and passes garbage to the original. The kit hooks `BuyItem` with a native detour and reads and writes the struct fields through raw pointers at offsets read from the IL2CPP runtime.

Purchases run in two phases. While the game's loop walks its vendor list, the hook copies each request and records price, stock and hop distance without buying. When `TryPurchaseIngredients` finishes, Better Supplier Choice sorts the collected vendors by the chosen mode, and the kit calls the original `BuyItem` in that order until the order is filled.

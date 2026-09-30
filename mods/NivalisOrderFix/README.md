# Manager Order Fix

Managers buy what they need and stop.

## The bug

Managers buy the full order from every vendor that stocks an ingredient. Needed 5 potatoes, bought 10. Needed 1 garlic, bought 4 from four shops.

The purchase loop in `VenueAreaGhost.TryPurchaseIngredients` tracks what's left to buy in a temp stack. `Vendor.BuyItem` checks the order against it and subtracts what it sells. The temp stack is created inside the vendor loop, so every vendor starts from the full amount and nothing carries over. A second stop check compares new purchases against current stock plus the order size, so it trips late.

Same save, same hour: 37 units bought before, 19 after.

Every managed venue in the city runs this code, NPC venues included.

## What the mod does

It restores the running total. The first vendor for an ingredient records the order size. Each later vendor is asked for what's left, or skipped once the order is filled. The rest of the manager's logic is untouched: when it reorders, how much it orders, and fresh stock only.

If a purchase fails, usually because the venue is out of money, the mod stops that recipe's purchases. The game's own loop stops at the same point.

From 2.0 the purchase handling lives in [Nivalis ModKit](../NivalisModKit), which this mod requires. Order Fix supplies the vendor order. Behavior is the same as 1.x.

## Settings

`BepInEx\config\bgasm.nivalis.orderfix.cfg`, created on first launch. Settings from 1.x (`will.nivalis.orderfix.cfg`) are copied over automatically.

`VendorSort` picks the vendor order:

| Mode | Order |
|---|---|
| `Vanilla` | Most stock first. The game's intended order. Default. |
| `Cheapest` | Lowest price first. Most buying shifts to Calypso Island. |
| `Local` | Nearest district first. Your own district can be the most expensive. |
| `Balanced` | Weighs price, distance and stock. |

`Vanilla` is the bug fix and nothing else. The other modes change how the city's economy moves.

`Balanced` scores each vendor as `price × (1 + DistanceWeight × hops) × (1 + ScarcityWeight / stock)` and buys from the lowest score.

| Setting | Default | Effect |
|---|---|---|
| `DistanceWeight` | 0.07 | Cost per district hop. Higher stays closer to home. Lower chases discounts. |
| `ScarcityWeight` | 2.0 | Steers away from shops running low. |
| `Verbose` | false | Logs each vendor purchase and skip. |

Hops are counted over the game's travel graph, `WorldLocation.transitions`. Your own district is 0.

## Requirements

- BepInEx. See the [root README](../../README.md).
- [Nivalis ModKit](../NivalisModKit) 0.1 or later.

## Install

1. Install BepInEx. See the [root README](../../README.md).
2. Launch the game once. Wait for the main menu, then close the game.
3. Extract the Nivalis ModKit zip into the game folder.
4. Extract this mod's zip into the same folder. The file lands at `BepInEx\plugins\NivalisOrderFix.dll`.
5. Launch the game.

To confirm it loaded, open `BepInEx\LogOutput.log` and look for `Purchasing pipeline: live` from the kit, then `Manager Order Fix loaded`.

If the kit is missing, BepInEx skips Order Fix and logs that a dependency is missing.

Upgrading from 1.x: install the kit, then replace `NivalisOrderFix.dll`. Your settings carry over.

To uninstall, delete `NivalisOrderFix.dll`. Remove the kit too if nothing else needs it. Saves are unaffected.

## Compatibility

Works alongside Use Oldest First.

Other mods that change how managers buy ingredients through the kit's `Purchasing` API work alongside it. Mods that hook `Vendor.BuyItem` directly will conflict.

Tested on Nivalis Nights 1.0 with BepInEx be.788.

## Changes

**2.0.0**
- Requires Nivalis ModKit. The purchase handling moved into the kit, so other mods can adjust vendor order or quantities without conflicting.
- Settings file renamed to `bgasm.nivalis.orderfix.cfg`. 1.x settings are copied over on first launch.
- No change to what managers buy.

**1.0.0**
- First release.

## Technical notes

`BuyItem` takes its `ShopTradeRequest` and `BasicTemp` structs by reference. Harmony's IL2CPP trampoline mishandles by-reference structs and passes garbage to the original. The kit hooks `BuyItem` with a native detour and reads and writes the struct fields through raw pointers at offsets read from the IL2CPP runtime.

Purchases run in two phases. While the game's loop walks its vendor list, the hook copies each request and records price, stock and hop distance without buying. When `TryPurchaseIngredients` finishes, Order Fix sorts the collected vendors by the chosen mode, and the kit calls the original `BuyItem` in that order until the order is filled.

## Fix for the devs

In `TryPurchaseIngredients`, move `ItemStack.BasicTemp.SafeCreate(item, amountToBuy, price, out temp)` above the vendor loop.
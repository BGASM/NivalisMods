# Manager Order Fix (Standalone)

Choose which vendors your managers buy ingredients from: the cheapest, the nearest, or a balance of price, distance and stock. No other mods required.

This is the standalone edition of [Manager Order Fix](../NivalisOrderFix). Same settings and vendor modes, without [Nivalis ModKit](../NivalisModKit). Install one edition, not both. If both are installed, BepInEx loads only one.

## The over-buy bug (fixed by the game)

Before the game's first patch (October 1, 2026), managers bought the full order from every vendor that stocked an ingredient. Needed 5 potatoes, bought 10. Order Fix 1.0 existed to fix that.

The patch fixed it in the game: managers now keep a running total and stop when the order is filled. From 1.1, Order Fix leaves quantities to the game and only changes the vendor order. **1.1 requires the patched game.** On an unpatched copy, keep 1.0.

## What the mod does

When a manager restocks an ingredient, the game lists the vendors that stock it, most stock first, and buys down that list until the order is filled. Order Fix re-sorts the list in the order you choose before the manager starts. Everything else is the game's own logic: when to reorder, how much, fresh stock only, the budget, and stopping when the venue runs out of money.

Only ingredient buying is affected. Furniture purchases use the game's own order.

## Settings

`BepInEx\config\bgasm.nivalis.orderfix.cfg`, created on first launch. The same file as the ModKit edition, so you can switch editions and keep your settings.

`VendorSort` picks the vendor order:

| Mode | Order |
|---|---|
| `Vanilla` | Most stock first, the game's own order. Default. Same as not having the mod. |
| `Cheapest` | Lowest price first. Most buying shifts to Calypso Island. |
| `Local` | Nearest district first. Your own district can be the most expensive. |
| `Balanced` | Weighs price, distance and stock. |

The non-Vanilla modes change how the city's economy moves: every managed venue in the city buys this way, NPC venues included.

`Balanced` scores each vendor as `price × (1 + DistanceWeight × hops) × (1 + ScarcityWeight / stock)` and buys from the lowest score.

| Setting | Default | Effect |
|---|---|---|
| `DistanceWeight` | 0.07 | Cost per district hop. Higher stays closer to home. Lower chases discounts. |
| `ScarcityWeight` | 2.0 | Steers away from shops running low. |
| `Verbose` | false | Logs each vendor purchase and skip. |

Hops are counted over the game's travel graph, `WorldLocation.transitions`. Your own district is 0.

## Requirements

- BepInEx. See the [root README](../../README.md).
- Nivalis Nights with the first patch (October 1, 2026) or later.

## Install

1. Install BepInEx. See the [root README](../../README.md).
2. Launch the game once. Wait for the main menu, then close the game.
3. Extract this mod's zip into the game folder. The file lands at `BepInEx\plugins\NivalisOrderFixStandalone.dll`.
4. If you have the ModKit edition (`NivalisOrderFix.dll`), delete it.
5. Launch the game.

To confirm it loaded, open `BepInEx\LogOutput.log` and look for `Manager Order Fix loaded`.

Upgrading from 1.0: replace `NivalisOrderFix.dll` with `NivalisOrderFixStandalone.dll`. Your settings carry over: 1.0's `will.nivalis.orderfix.cfg` is copied to the new file on first launch.

To uninstall, delete `NivalisOrderFixStandalone.dll`. Saves are unaffected.

## Compatibility

Works alongside Use Oldest First.

Don't combine with the ModKit edition of Order Fix, or with other mods that reorder managers' vendors. Mods that use the kit's `Purchasing` API need the ModKit edition instead.

Tested on Nivalis Nights 1.0 (first patch, October 1, 2026) with BepInEx be.788.

## Changes

**1.1.0**
- For the patched game (tested on patch 2), which fixed the over-buy bug itself. Order Fix now only sets the vendor order, by sorting the game's vendor list; quantities are left to the game.
- Verbose logging is one line per vendor bought from and one per run of skipped vendors (with each one's price, stock and hops), with the reason: order filled, or over budget.
- If the logging hooks ever fail after a game update, only the logging stops; the vendor order keeps working.
- Settings file renamed to `bgasm.nivalis.orderfix.cfg`, shared with the ModKit edition. 1.0 settings are copied over on first launch.
- No longer hooks `Vendor.BuyItem`.

**1.0.0**
- First release.

## Technical notes

`EconomyManager.GetVendorsByItem` builds the vendor list that `VenueAreaGhost.TryPurchaseIngredients` walks. It returns a struct and hands the list back through an `out` parameter, which Harmony can't wrap safely under IL2CPP, so the mod hooks it with a native detour. The detour calls the original, then, only while ingredients are being bought, re-sorts the list's elements in place. The list layout (field offsets, element size) is read from the IL2CPP runtime, not hard-coded.

With `Verbose` on, the mod notes each vendor the game prices (`Vendor.GetItemBuyCost`, called right before it buys) and credits each purchase (`VenueAreaGhost.TryMakePurchase`) to that vendor. Vendors the game never priced were skipped because the order was already filled.

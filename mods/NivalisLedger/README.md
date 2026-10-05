# Nivalis Ledger

A live profit tracker for your venues, in your browser next to the game.

Open **http://localhost:5720** while playing (or pause menu > **Mods** > Nivalis Ledger > **Open in browser**). Only your own computer can open it.

## What it shows

Each venue gets its own page, with tabs:

- **Dashboard:** today's revenue, costs and profit; what's selling (pie chart); the latest reviews; sales as they happen; what's running low (stock levels through the day, and which dishes are about to run out); income against costs today; money by day.
- **Menu:** each dish's price, plate cost (from the ingredients you chose for it), profit per plate, margin, and sales.
- **Ingredients:** stock, what's on its way, average cost, market price, stock value, purchases and spoilage today.
- **Orders:** customer orders waiting to be served; finished ones flash green and drop off.
- **Staff:** wages, shifts, what's been paid today and what's still due, missed payments.
- **Reviews:** rating, service, cleanliness, comfort, every review, and popularity by customer group.

Your inventory has its own page too.

## How costs work

Every ingredient has an average cost, per venue and in your inventory. Manager orders count at what was paid, when they're delivered. What you buy at vendors counts at what you paid, harvests at zero, and moving stock between your inventory and a venue moves its cost with it. Stock that appears from nowhere (rewards, stock from before the Ledger) starts at market price, marked estimated; correct it by typing **10@3.30** (10 at 3.30) on its line.

Spoiled food is counted as waste, at the cost of what spoiled.

## Settings

`BepInEx\config\bgasm.nivalis.ledger.cfg`: `[Web] Port` (default 5720) if another program already uses it.

## Installation

Needs BepInEx 6 (IL2CPP, be.788) and [Nivalis ModKit](https://github.com/BGASM/NivalisModKit) 0.5.0 or later. Extract the zip into the game folder: `BepInEx\plugins\NivalisLedger.dll`.

The Ledger's records are kept with each save.

# WoW Forever launch checklist

Things that could only be prepared, not tested, while the beta was closed. Go through them on the first evening
after launch. The client is `C:\Program Files (x86)\World of Warcraft\_forever_` (`.flavor.info` = `wowf`, confirmed
before launch).

## 1. Setup and addon install
- [ ] Start the app. The bar above the search box shows `_forever_ (Forever)   not installed` → **Install addon**.
- [ ] `_forever_\Interface\AddOns\WoWOracle` exists afterwards; the bar shows `✓ 0.13.0` in green.
- [ ] In game, the addon is listed and **not** "out of date". The TOC says `## Interface: 16001` (beta). If the
      release client has a new interface number (`/dump select(4, GetBuildInfo())`), update the TOC, rebuild, Update addon.
- [ ] The leftover `_classic_beta_` folder is ignored (shown nowhere). Delete it if Battle.net left it behind.

- [ ] Move and resize the window, Exit from the tray, start again: it opens in the same spot and size (maximized too).

## 2. `/oracle debug` (post the output)
- [ ] Client version / build / interface.
- [ ] Talents: `C_Traits`, `C_ClassTalents`, `INSPECT_TRAIT_CONFIG_ID`, trait talents count.
- [ ] Professions: `GetNumSkillLines + GetSkillLineInfo`, `GetProfessions`, `GetNumTradeSkills + GetTradeSkillRecipeLink`,
      `GetNumCrafts + GetCraftRecipeLink` (enchanting), `C_TradeSkillUI.GetAllRecipeIDs`. The `skills:` line should list
      your professions with rank/max.
- [ ] Bags: `C_Container.GetContainerItemInfo` or `GetContainerItemInfo`.

## 3. Character sync (crafting planner)
- [ ] Log in, open each profession window once, open the bank once, then `/oracle sync`.
- [ ] Chronicle shows "Synced from the game: <name> — <profession> x/y, N items in bags and bank".
- [ ] Crafting → Level a profession: your character is in the list; skill now / goal fill in; the note shows
      "N known recipes" and "bags + bank".
- [ ] Materials "have" counts match your bags.
- [ ] Enchanting (Craft window in Classic) also reports known recipes.

## 4. Own gear sync
- [ ] A gear set and a talent set named exactly after your character appear (Chronicle: "Gear and talents from the game").
- [ ] Change a piece of gear → `/oracle sync` → the set updates within a few seconds.
- [ ] A copy under another name ("<name> Alt SP") is left alone.

## 5. Quests and talent trees from the client
- [ ] `/oracle quests` counts quests; after a restart the Chronicle lists quests added that the data didn't have.
- [ ] If the client build is newer than wago's data, talent trees from the client apply (Chronicle line). Otherwise wago's are used.

## 5b. New world data (addon 0.8.0, only what the data lacks)
- [ ] Chronicle: "Addon's known-data list updated (_forever_ …)". `Interface\AddOns\WoWOracle\Known.lua` is no longer the `WoWOracleKnown = nil` placeholder.
- [ ] In game `/reload`, then `/oracle seen`: second line shows the known list (npcs, placed, objects, quests, vendors, sourced ranges).
- [ ] Target a few NPCs in a Forever-only zone (Mount Hyjal, Zephras Isle…) and a vendor there; kill and loot some mobs. `/oracle seen` counts go up; in an old zone they stay at 0.
- [ ] `/oracle sync` → Chronicle: "From the game: … new NPCs · … vendor items · … drops". The new NPC shows on the map, its drops in the item's sources.
- [ ] If loot never counts: `/dump GetLootSourceInfo` must be a function (otherwise only your dead target counts).

## 5c. Base stats (addon 0.9.0)
- [ ] `/oracle base` prints your base stats; after `/oracle sync` the Chronicle says "From the game: … base-stat level".
- [ ] Calculator with that class, race and level and blank naked stats: the note says "Naked stats recorded in game at level N".
- [ ] Take all gear off once: `/oracle base` mentions health and mana; **Copy recorded** gives CSV with naked health/mana.
- [ ] Make the class/race combinations you're curious about (Tauren Warrior…) and log each in once.

## 5d. Trainer skills (addon 0.10.0)
- [ ] Open a profession trainer, then `/oracle trainer`: "read N recipes from this trainer". The trainer window's
      filter (available / unavailable / used) is the same as before you opened it.
- [ ] `/oracle sync` → Chronicle: "From the game: … N trainer skills".
- [ ] That trainer's recipes show their skill without **~** on the item page; Level a profession uses them.
- [ ] Compare a few against the trainer's "Requires <profession> (N)" line.

## 5e. Forever dungeons and map search (app 1.1.7)
- [ ] Loot: The Drowned City lists Zul'Alai, Var'Taka, Captain Dreadrise… After a run with the addon on and `/oracle sync`,
      the bosses you killed show their drops.
- [ ] Map: search "freewind", "shimmering", a Riverglades subzone and an NPC; each opens the zone with a marker on the spot.
- [ ] Addon 0.11.0: spend a minute in a subzone the search shows as "(whole zone)" (Faol's Rest), `/oracle sync`: the
      Chronicle says "From the game: 1 subzone on the map" and the search puts a marker there.

## 5f. Ore & herbs (app 1.1.13, addon 0.12.0)
- [ ] First start rebuilds the game data (schema 30). Map → **Ore & herbs**: Mining lists Copper 1 … Rich Thorium 275,
      Herbalism Peacebloom 1 … Black Lotus 300; your skill's row is gold.
- [ ] Pick Copper Vein: the map opens on its best zone with copper dots; the zone list under it opens other zones with dots.
- [ ] Map search "tin" / "peacebloom" opens the guide on it. **Clear** takes the dots off.
- [ ] In game, mine or pick a few nodes in a Forever zone (Riverglades), `/oracle sync`: the Chronicle says
      "… ore/herb spots on the map" and the node shows there (a new name lists with skill "?").

## 5g. Recipe trainers (app 1.1.16)
- [ ] First start rebuilds the game data (schema 32). Heavy Runecloth Bandage: **Taught by** Doctor Gregory Victor
      (Arathi Highlands) and Doctor Gustaf VanHowzen (Dustwallow Marsh), skill 290, quest Triage; requires 290, no "~".
- [ ] Professions → First Aid: Runecloth Bandage says "from Doctor Gregory Victor (Arathi Highlands) or …".
      Alchemy Greater Mana Potion names Rogvar / Kylanna Windwhisper.

## 5h. NPC spawns (app 1.1.19, addon 0.13.0)
- [ ] First start rebuilds the game data (schema 34). Map search "Harvest Golem": Westfall opens on the whole map with a
      red dot on each spawn and no marker; a dot's tooltip says "27 spawn points in Westfall".
- [ ] An item "Dropped by" a mob → View on map: the same red dots. A single-spawn NPC (a trainer) shows the marker.
- [ ] In a Forever zone, walk up to a few of the same Forever mob in different places (target or mouse over them up
      close), `/oracle sync`: the Chronicle says "… NPC spots on the map"; map search on it shows the dots.

## 5i. Upgrade list (app 1.1.22)
- [ ] Gear → Upgrades on a slot → **+ List** on two items: the **Upgrade list (2)** tab shows them with slot, gain and source; the tabs swap between Totals, Upgrades and the list.
- [ ] Tick one: it greys out and moves down after a refresh; **Clear got** removes it. **Equip** on the other puts it
      in the slot and ticks it. Another gear set has its own (empty) list; restart keeps them.

## 5j. Faction filter (app 1.1.25)
- [ ] First start rebuilds the game data (schema 36). Upgrades on the head of a level 60 Horde paladin or warrior:
      **Horde only** is ticked and Helm of Heroism comes from Mokvar (Orgrimmar), with no Soulforge Helm (Ironforge);
      untick it and the Alliance quests show too. An Alliance set says **Alliance only**.

## 6. Game data
- [ ] Newer `wowf` build downloaded (status line / Chronicle); first-start card shows while building on a fresh PC.
- [ ] Talents are Forever's (e.g. Mage Arcane top row: Wand Specialization, Arcane Focus, Improved Channeling, Missile Barrage).
- [ ] Learned items are now in the real data (no "Learned from the game client" descriptions on common items).

## 7. Content to follow up
- [ ] Lore for Forever's new zones (Mount Hyjal, Zephras Isle, Riverglades, Shen'dralas, Darkspear Islands) in `src/WowCompanion.Core/Lore/zones.json`.
- [ ] A Forever quest source (Wowhead Forever?) to replace or top up the CMaNGOS Classic quests.

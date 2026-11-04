# WoW Forever launch checklist

Things that could only be prepared, not tested, while the beta was closed. Go through them on the first evening
after launch. The client is `C:\Program Files (x86)\World of Warcraft\_forever_` (`.flavor.info` = `wowf`, confirmed
before launch).

## 1. Setup and addon install
- [ ] Start the app. The bar above the search box shows `_forever_ (Forever)   not installed` → **Install addon**.
- [ ] `_forever_\Interface\AddOns\WoWOracle` exists afterwards; the bar shows `✓ 0.9.0` in green.
- [ ] In game, the addon is listed and **not** "out of date". The TOC says `## Interface: 16001` (beta). If the
      release client has a new interface number (`/dump select(4, GetBuildInfo())`), update the TOC, rebuild, Update addon.
- [ ] The leftover `_classic_beta_` folder is ignored (shown nowhere). Delete it if Battle.net left it behind.

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
- [ ] Chronicle: "Told the addon what the app already knows (_forever_ …)". `Interface\AddOns\WoWOracle\Known.lua` is no longer the `WoWOracleKnown = nil` placeholder.
- [ ] In game `/reload`, then `/oracle seen`: second line shows the known list (npcs, placed, objects, quests, vendors, sourced ranges).
- [ ] Target a few NPCs in a Forever-only zone (Mount Hyjal, Zephras Isle…) and a vendor there; kill and loot some mobs. `/oracle seen` counts go up; in an old zone they stay at 0.
- [ ] `/oracle sync` → Chronicle: "From your travels in Azeroth: … new NPCs, … vendor items, … drops". The new NPC shows on the map, its drops in the item's sources.
- [ ] If loot never counts: `/dump GetLootSourceInfo` must be a function (otherwise only your dead target counts).

## 5c. Base stats (addon 0.9.0)
- [ ] `/oracle base` prints your base stats; after `/oracle sync` the Chronicle says "Base stats from the game: … recorded".
- [ ] Calculator with that class, race and level and blank naked stats: the note says "Naked stats recorded in game at level N".
- [ ] Take all gear off once: `/oracle base` mentions health and mana; **Copy recorded** gives CSV with naked health/mana.
- [ ] Make the class/race combinations you're curious about (Tauren Warrior…) and log each in once.

## 6. Game data
- [ ] Newer `wowf` build downloaded (status line / Chronicle); first-start card shows while building on a fresh PC.
- [ ] Talents are Forever's (e.g. Mage Arcane top row: Wand Specialization, Arcane Focus, Improved Channeling, Missile Barrage).
- [ ] Learned items are now in the real data (no "Learned from the game client" descriptions on common items).

## 7. Content to follow up
- [ ] Lore for Forever's new zones (Mount Hyjal, Zephras Isle, Riverglades, Shen'dralas, Darkspear Islands) in `src/WowCompanion.Core/Lore/zones.json`.
- [ ] A Forever quest source (Wowhead Forever?) to replace or top up the CMaNGOS Classic quests.

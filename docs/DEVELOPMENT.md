# WoW Oracle: developer notes

How the app is put together, where its data comes from and the formats the addon and the app share. For how to
*use* the app, see the [README](../README.md).

## Building

Windows, .NET 10 SDK.

```
dotnet build src\WowCompanion.Desktop
dotnet run --project src\WowCompanion.Desktop
```

`.\publish.ps1` builds `dist\WoWOracle-<version>.zip`: one self-contained `WoW Oracle.exe` (no .NET needed), the
addon (`addon\WoWOracle`, with the empty `Known.lua` placeholder), `packaging\README.txt`, `LICENSE.txt` and
`THIRD-PARTY-NOTICES.md`. The version comes from `<Version>` in `Directory.Build.props`. `-IncludeFont` copies
`font\*.ttf` into the zip (Friz Quadrata is licensed: private copies only). The unzipped copy is `dist\WoWOracle`, a
fixed folder to run and test the build from; each run removes the earlier builds in `dist`, so only the latest folder
and zip are there. Close the app first if it runs from `dist\WoWOracle` (the script stops and says so).

### Releases

`.github/workflows/release.yml` builds the zip on GitHub (Windows runner, same `publish.ps1`) and attaches it to a
GitHub Release:

1. Commit and push your changes.
2. `git tag v1.0.1` and `git push origin v1.0.1`. The tag is the version: the build passes it to `publish.ps1
   -Version`, so the app and zip get it. `Directory.Build.props` only matters for local builds (bump it now and then
   so they match).
3. The Release appears with `WoWOracle-1.0.1.zip` and notes generated from the commits; edit the notes on GitHub if you like.

"Run workflow" on the Actions tab makes a test build without a Release (the zip is attached to the run).

`.\install-addon.ps1` links the repo's addon folder into your client's `Interface\AddOns` (`-Copy` for a plain copy).
With a linked copy the app writes `Known.lua` into the repo folder; it is git-ignored.

## Projects

| Project | Role |
|---|---|
| `WowCompanion.Core` | Paths, game/user databases, search, services for every page, the calculator (`Calc/`), addon formats. No UI. |
| `WowCompanion.Data` | wago.tools client, CSV reader, game database builder, update check, CMaNGOS import. |
| `WowCompanion.Desktop` | The WPF window (WPF-UI 4, CommunityToolkit.Mvvm). `MainViewModel.*.cs` is one partial file per page. |
| `WowCompanion.Spike` | Console tool for poking at wago.tools tables (`builds`, `fetch --product=…`, `report`). |

## Files on the user's PC

Everything lives in `%LOCALAPPDATA%\WowCompanion`:

| Path | What | Safe to delete? |
|---|---|---|
| `data\game-s<schema>-<build>.sqlite` | The game database, rebuilt from the cache | Yes (rebuilt) |
| `cache\<build>\*.csv`, `cache\sources\*.sql.gz` | Downloaded tables and the CMaNGOS dump; old builds are pruned | Yes (downloaded again) |
| `icons\`, `maps\`, `talent-layouts\` | Image and layout caches | Yes |
| `user.sqlite` | Everything the user made: gear/talent sets, notes, craft list, settings, learned quests/items/sightings | **No**: back this one up |
| `wow-path.txt` | WoW folder picked with Browse… | Yes |
| `data-product.txt` | Optional: another wago.tools product code to build from | — |

## Game data

- **Client tables** come from [wago.tools](https://wago.tools) for product `wowf` (WoW Forever). Required and optional
  tables are listed in `GameDatabaseBuilder.RequiredTables` / `OptionalTables`; a missing optional table is skipped
  and noted in `meta.notes`.
- **Classic Era fallback**: Forever ships some items without data; `ItemSparse`/`ItemEffect` from
  `wow_classic_era` fill item ids below 200000 (no SoD).
- **Sources** (drops with real chances, vendors, quests, NPC/object spawns, quest chains and text) come from the
  [CMaNGOS classic-db](https://github.com/cmangos/classic-db) dump (Classic 1.12). Loot groups and reference
  tables are worked out at import.
- **Icons, map tiles and talent art** are the game's BLP files from `wago.tools/api/casc/<fileDataId>?download`,
  decoded by `Blp.cs` (DXT1/3/5, palettized, raw) and cached as PNG. File ids → names come from
  [wowdev/wow-listfile](https://github.com/wowdev/wow-listfile).
- **Talent layouts** for Icy Veins / Wowhead links: `static.icy-veins.com/json/forever-talent-calculator/<class>.json`,
  only fetched when the share panel is used, cached a week.

### Updates (`GameDataUpdater`)

On start the app opens the newest local database right away, then checks wago.tools for a newer build. Only when
there is one does it download the tables and build a new file next to the old one. The app switches to it and only
then deletes older files (`GameDb.DeleteOlder`).

- Downloads are cached per build. If the Classic Era or CMaNGOS download fails, the copy on disk is used, so a
  rate limit never produces a database without sources.
- Offline with no database for the current schema (after an app update): it rebuilds from the newest complete
  cached build.
- After a successful build, cached tables of other builds are pruned (the current build and the Era fallback stay).
- **Schema changes**: bump `GameDb.SchemaVersion` whenever tables change. The schema number is part of the file
  name, so the next start builds a new database from the cached CSVs.

### Game text

`GameText.Clean` tidies text from the client: UI colour codes (`|cFFFFFFFF…|r`, `|cnEPIC_PURPLE_COLOR:…|r`), `|n`,
inline icons and links are removed, and hand-wrapped tooltip lines are joined (paragraphs stay). Spell, talent and
item descriptions go through it when the database is built; talent trees from the addon too.

### Placeholder items

The client's `Item` table sometimes has items wago.tools has no `ItemSparse` row for yet (new Forever recipes such as
Cleansing Stew). Crafted ones get a stand-in named after their recipe, with their real class and icon, listed in
`placeholder_items`; learned data from the addon replaces them, and real data does on the next build.

### Hidden items

`hidden_items` lists Season of Discovery leftovers (ids 202000–238999, and SoD supply items such as Waylaid Supplies above 200000) and
test/placeholder items (by name, including Blizzard's item-level template gear such as "90 Epic Warrior Helm"). `ItemVisibility.HideSod` filters them from search, recipes, upgrades and the
planner; items seen in game (gear sets, learned items, quest rewards) are taken off the list.

## How the numbers are worked out

- **Weapon damage**: `ItemDamage*` DPS table [item level][quality] × speed, spread by `DmgVariance`. Matches
  Arcanite Reaper and Sulfuras. Ranged weapons use the Thrown table (unverified).
- **Armor**: `ItemArmorTotal` × `ItemArmorQuality` × `ArmorLocation`; shields from `ItemArmorShield`. Matches
  Lionheart Helm (565).
- **Stats**: `StatPercentEditor` share of the `RandPropPoints` budget; random-suffix stats per gear-set slot.
- **Spells**: values, variance and coefficients from `SpellEffect`; tooltips fill `$s1`, `$o2`, `$d`… at the
  spell's own level (`SpellText`).
- **Talents**: Forever's trait tables (`TraitNode`, `TraitDefinitionEffectPoints` + `CurvePoint` for per-rank
  values such as Improved Seals 5/10/15%). Rules: first point at 10, 5 points per tier, prerequisites at their required rank.
- **Calculator** (`Core/Calc`): `StatEngine` = naked stats (estimate or typed) + gear + enchants + equip auras +
  passive talents + buffs, then Classic conversions (14 crit / 10 hit rating per 1%). `DamageEngine` = base + power ×
  coefficient, talent modifiers (aura 107/108 by class mask), crit, chance to land vs target level, DoTs and
  channels. Healing uses the same engine: (base + healing power × coefficient) × (1 + healing talents), direct heals
  crit for +50%, heals over time and absorbs (aura 69) don't crit, Chain Heal halves per jump, group heals are per
  target. Healing power = spell power items (stat 45, damage and healing) + healing items (stat 41, aura 135);
  stat 42 is spell damage only. Not yet: procs, glancing/dodge/parry, "% of mana" talents, healing-received
  modifiers.
- **Upgrades**: alternatives ranked by the stats the set already leans on (stat budget prices, e.g. 1% crit = 14
  points), filtered by proficiency and level.
- **Map search** (`map_places`, `MapService.Search`): subzones (AreaTable with a parent) at the middle of their
  explored-area patch (`WorldMapOverlay.AreaID_0-3`), towns and landmarks (`AreaPOI`) and flight masters (`TaxiNodes`)
  from their world position, which win over a patch centre; NPCs from `npcs`. A subzone without a spot borrows one
  from an NPC named after it ("Bandarion Keep Paladin"), or from the addon's `!WOWORACLE-PLACE` samples; otherwise
  it opens the whole zone.
- **Forever's own zones and dungeons**: the Classic world data (CMaNGOS) has nothing for them. Classic spawns are placed
  with `ZoneLocator(classicOnly)`, which leaves out Forever's zone maps (UiMap 2000+): their rectangles overlap the
  old zones (Riverglades over the Burning Steppes). Dungeons from map 2900 on are listed from `DungeonEncounter`
  (`dungeon_bosses`); a boss's loot comes from the addon's sightings, matched by NPC name.
- **Auction prices** (`AuctionatorData`, optional): `WTF\Account\*\SavedVariables\Auctionator.lua`,
  `AUCTIONATOR_PRICE_DATABASE`: one Lua string per realm, each a CBOR map of item id → `m` (lowest price at the last
  scan, copper), `h` / `l` (high / low per scan day, days since 2020-01-01), `a` (count up per day). Read at start-up
  and when the file changes; the newest realm is used unless one is picked (setting `auction_realm`).
  `Desktop\AuctionPrices` holds the current prices for every view.
- **Level a profession**: at every skill point the recipe with the lowest expected material cost per point
  (skill-up chance 100% while orange, then (grey − skill) / (grey − yellow)), materials at vendor price, else their
  auction price when Auctionator has one, else vendor value as a stand-in, what you
  own counts cheaper, cooldown recipes left out, stack sizes ("makes N each") respected.
- **Learn skills** (`recipes.min_skill`, `skill_source`): `recipe` = the teaching item's required skill; `starter` =
  learned with the profession (`SkillLineAbility.AcquireMethod` 1), skill 1; `trainer` = read from a trainer window by
  the addon; `estimate` = the yellow point. The client has no learn skill for trainer recipes, and against Classic's
  trainer lists the real one is 49 below to 5 above yellow, so yellow almost never claims "learnable" too early.
  When nothing is learnable at a skill, the planner falls back to estimated recipes from 20 below yellow, with a note.

## The addon and the app

The addon (`addon/WoWOracle`) only reads the game through normal APIs and writes its SavedVariables file
(`WTF\Account\<account>\SavedVariables\WoWOracle.lua`), which the game writes on logout or `/reload`. The app finds
WoW Forever clients by `.flavor.info` (`wowf`, `wowf_beta`, `wowf_ptr`); other games' clients are never touched. It
watches each client's `WTF` folder and reads every `WoWOracle.lua` it finds (`AddonSavedData`).

Everything the addon saves is a text block: a marker line, `key=value` lines, `!END`. Unknown keys are ignored.

| Marker | What | Read by |
|---|---|---|
| `!WOWORACLE 1` | Character export (Oracle button / `/oracle`): `item=slot;id;enchant;suffix;name`, `talent=name;rank;spell;node`, `stat=key;value`, `bonus=slot;type:value,…`, `itemdata=…` | Import character, own gear sync |
| `!WOWORACLE-SELF 1` | Your own export, saved by itself | Own gear sync (sets named exactly after the character) |
| `!WOWORACLE-CHAR 1` | Profession ranks, known recipes, bag and bank counts | Crafting planner |
| `!WOWORACLE-TALENTS 1` | The live talent tree: `node=id;x;y;maxRanks;spell;icon;unlocks;name;description` | Used only when the client build is newer than the wago.tools data |
| `!WOWORACLE-PLACE 1` | Where you were in a subzone, averaged: `place=zone;subzone;x;y;samples` (sampled every 8 s, up to 60) | `seen_places` (user.sqlite) → `map_places`, only subzones without a spot, from 5 samples on |
| `!WOWORACLE-TRAINER 1` | One per profession, all trainers merged: `profession=`, `recipe=skill;itemId;level;name` (the trainer window's skill requirement) | `trainer_skills` (user.sqlite) → `recipes.min_skill`, `skill_source = 'trainer'` |
| `!WOWORACLE-BASE 1` | Base stats per class/race/level: `base=class;race;level;str;agi;sta;int;spi` (paper-doll stat minus gear and buffs), `vitals=health;mana;naked` | `base_stats` (user.sqlite) → Calculator naked stats (exact level, else interpolated) |
| `!WOWORACLE-QUEST 1` | Quests the data lacks: giver, where, rewards, `itemdata=` | `learned_quests` → game database |
| `!WOWORACLE-NPC 1` | NPCs without a known spot: name, level, rank, reaction, `at=zone;x;y` | `seen_data` → game database |
| `!WOWORACLE-VENDOR 1` | Unknown vendors, or known ones selling an item without a source: `item=id;price;stack;left;extendedCost` | `seen_data` → item sources |
| `!WOWORACLE-ITEM 1` | Items the data has no item row for, seen in bags, bank, profession windows, loot or vendors: one `itemdata=` line | `learned_items` → items (replaces placeholders) |
| `!WOWORACLE-LOOT 1` | Loot from unknown NPCs/objects, or items without a source: `source=npc\|object;id;name`, `kills=`, `item=id;drops;count` | `seen_data` → item sources (chance = drops / kills) |

### Only new data (`Known.lua`)

The app writes `Known.lua` into each installed addon copy: the ids its data already has, as ranges (`npcs`,
`placed` = NPCs with a spot, `objects`, `quests` minus the ones the addon reported, `vendors`, `sourced` = items with
any source, `items` = items with real data). The addon saves quests, NPCs, vendors and loot only when they're not in it; with the placeholder
`WoWOracleKnown = nil` it saves none of them. The file is rebuilt only when the database changed or something was
added.

### Filling gaps

One pass (`MainViewModel.AddonPass`, off the UI thread, at start-up and whenever the file changes) reads the files
once, keeps what's new in `user.sqlite`, then adds to the game database what it lacks. Downloaded rows always win;
rows the app added are tracked in `meta` (`learned_quests`, `seen_rows`) so they can be refreshed. wago.tools and
CMaNGOS stay the master.

## Conventions

- The UI speaks plainly and with some WoW flavour; keep descriptions short.
- `UserDatabase.Migrations`: add a new entry for every user-schema change, never edit an old one.
- Only the `wowf` product (plus the Classic Era fallback) is used, never another region's product; the addon only fills gaps.

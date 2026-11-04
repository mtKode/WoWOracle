# WoW Oracle

**A pocket companion for WoW Forever.** Search every item, recipe, spell and quest, plan your talents and gear,
level a profession the cheapest way, look up dungeon loot and keep notes, all in one small window that sits next
to the game. An optional in-game addon keeps it in sync with your character.

> Free and fan-made. Not affiliated with or endorsed by Blizzard Entertainment. No game data or art is included:
> the app downloads it on your PC. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Contents

- [Getting started](#getting-started)
- [The in-game addon](#the-in-game-addon)
- [Searching](#searching)
- [The pages](#the-pages)
- [Keyboard shortcuts](#keyboard-shortcuts)
- [Questions and troubleshooting](#questions-and-troubleshooting)
- [Where the data comes from](#where-the-data-comes-from)
- [For developers](#for-developers)

---

## Getting started

1. Download the latest `WoWOracle-<version>.zip` from the **Releases** page and unzip it anywhere (Desktop,
   Documents, a games folder…). Keep the `addon` folder next to the exe.
2. Run **`WoW Oracle.exe`**. Nothing to install, no .NET needed. Windows SmartScreen may warn because the exe isn't
   signed: click **More info → Run anyway**.
3. The first start downloads the game data (items, spells, talents, maps, where things drop) and puts it together.
   That takes a minute or two. After that the app opens right away and checks for new game builds by itself.
4. The bar above the search box shows the WoW Forever install it found. Click **Install addon** to put the
   addon into the game (see below). If WoW isn't in a usual place, click **Browse…** and pick your World of Warcraft
   folder.

**Ctrl+Shift+\_** hides the window to the tray and brings it back, even while you're in the game. Press **/**
anywhere to start a new search.

## The in-game addon

The addon is optional, but it makes the app *know you*: your gear, talents, professions and bags show up by
themselves. It only works with WoW Forever, and it never sends anything anywhere: the game saves its data to your
own disk and the app reads it from there.

**Install:** click **Install addon** in the app (or **Addon** at the bottom right later, which also offers
updates). In game, `/reload` or restart, and tick *WoW Oracle* in the AddOns list if it's off.

**What it does by itself**

| | |
|---|---|
| **Your gear and talents** | Kept as a gear set and a talent set named exactly after your character. Sets with any other name are never touched, so copy one ("… Alt SP") to experiment. |
| **Professions, recipes, bags and bank** | Feed the profession planner: your skill, the recipes you know and the materials you have. Open each profession window and the bank once. |
| **New quests, NPCs, vendors and loot** | Things the app's data doesn't have yet (new Forever zones and quests) are recorded once and added to the app: quest givers and rewards, NPCs on the map, what vendors sell, what mobs drop with a drop chance from your own kills. Things the app already knows are not recorded. |

The game writes the addon's data when you **log out** or **/reload**; the app picks it up within seconds and says
what came in, in **Oracle's Chronicle** (the scroll at the bottom left).

**Commands**

| Command | What it does |
|---|---|
| `/oracle sync` | Save now and reload, so the app gets your latest state right away |
| `/oracle` | Copy your character (also the **Oracle** button on your character window) |
| `/oracle t` | Copy the player you're targeting (also the **Oracle** button on the inspect window) |
| `/oracle quests` · `/oracle seen` | How many new quests / NPCs, vendors and loot sources are waiting for the app |
| `/oracle debug` | Prints what the client supports; handy for bug reports |

To bring in someone else's character: inspect them, click **Oracle**, press Ctrl+C, then in the app go to
**Gear → Import character** and paste. You get a gear set and a talent set named after them.

## Searching

Type in the search box at the top. Items and quests show as you type; **↑ ↓** (or Tab) move, **Enter** opens,
**Esc** closes the list.

Plain words search names. Add **filters** anywhere to narrow it down; they all have to match:

| Filter | Example | Finds |
|---|---|---|
| Stats | `stat:str` · `stat:sp,crit` | Items with the stat(s), best first |
| Slot | `neck` · `2h` · `slot:offhand` | Items for that slot (bare slot words work next to another filter) |
| Type | `type:plate` · `type:sword` | Armor or weapon type |
| Quality | `q:epic` · `q:rare+` | Item quality |
| Level | `lvl:50-60` · `lvl:<40` · `ilvl:60+` | Required level or item level |
| Class | `class:mage` | Items the class can use |
| Quest | `quest:defias` · `quest:"the defias brotherhood"` | Quest rewards |
| Source | `from:ragnaros` | Dropped, sold or found by that NPC or chest |

Example: `stat:str q:rare+ lvl:40-50 waist`. The **?** button in the search box lists every filter and stat name.

**Suggestions as you type**: start a filter and the list offers to finish it. `st` suggests `stat:`, `stat:cr` suggests
`stat:crit`, `q:` lists the qualities, `from:rag` finds *Ragnaros* and `quest:defias` finds the quest titles.
Press **Tab** to take the suggestion (or click it); inside a filter **Enter** takes it too. A finished filter
becomes a chip right away.

A finished filter turns into a coloured **chip** when you type a space. To change one, press **←** at the start
of the text to step onto the chips: **← →** pick one, **Del** removes it, **Enter** edits it. Or click a chip
(double-click to edit) or its ×.

In the results: **Shift+Enter** (or Shift+click) puts the item in your current gear set, **Ctrl+Enter** (or
Ctrl+click) adds its recipe to your craft list.

**Season of Discovery leftovers** and test items are in Forever's game files but not in the game, so they're hidden.
The checkbox at the top of the **?** card shows them again.

## The pages

The tabs on the left side of the window (or **Ctrl+1 … Ctrl+0**, **Ctrl+M**).

### Items (Ctrl+1)
Everything about an item, like the game's tooltip: stats, effects, set bonuses, and **where to get it**: who drops
it and how often, which vendor sells it, which quest rewards it, with zone and map coordinates (**View on map**).
Also what it's crafted from and what it's used in. Quests from the search show who gives them, where, the chain
and the quest text. Hold **left Alt** over any item anywhere in the app to compare it with your gear set.

### Recipes (Ctrl+2)
All recipes of a profession with their reagents, what they make and the skill colours (orange, yellow, green,
grey). Filter by recipe or reagent name, sort, and press **+ Craft list** (or Ctrl+Enter) to queue one.

### Crafting (Ctrl+3)
- **Craft list**: what you plan to make, with one shopping list for everything. Tick recipes off as you craft them.
- **Level a profession**: pick a profession and a goal, and the Oracle plans the cheapest route skill point by
  skill point, with the shopping list (need · have · still to get). With the addon, your skill, known recipes
  and bag/bank contents fill in by themselves. Options: vendor recipes, found recipes, making sub-parts
  (bolts, cured leather…) and smelting your own bars. Trainer checkpoints are marked.

### Spells (Ctrl+4)
Every class spell with its real numbers per rank: damage, cost, cast time, cooldown, range, effects. Filter by name
or description.

### Talents (Ctrl+5)
Forever's talent trees. Left click adds a point, right click removes one, hover to read a talent. Keep several
**talent sets** per class (new, duplicate, rename). **Share / import** gives you an Icy Veins and a Wowhead link
for the build on screen, and takes a link from a friend as a new set. **Copy text** gives a readable build for
chat. Show one tree at a time or all three; they grow with the window.

### Gear (Ctrl+6)
**Gear sets**: put items in slots and see the totals. **Enchant** on an item picks an enchant, oil or kit for it.
**Upgrades** on a slot lists better items for it, ranked by the stats your set already leans on, with where each
one drops (toggle raid, dungeon and world drops), and **Equip** tries it on. **Import character** pastes a copy
from the addon.

### Calculator (Ctrl+7)
Pick class, race, level, a gear set and a talent set, add buffs and consumables, choose a target level, and see
your full character sheet (hit, crit, spell power, attack power…) and the damage of your spells: per cast, DPS
and crit, with the talents that changed it. Compare races, and save calculations to compare later. Type your naked
stats from the character screen for exact numbers.

### Loot (Ctrl+8)
Dungeon and raid loot, AtlasLoot style. Search a dungeon, raid or boss, pick a boss and see its drops by type
(gear, recipes, quest items) with real drop chances. **Quests** at the top of a dungeon lists the quests for it
and their rewards.

### Notes (Ctrl+9)
Notes for later (a vendor to visit, a recipe to buy, a reset) with optional **reminders**: "18:30", "tomorrow
9:00", "in 2h", repeating daily or weekly. Reminders pop up even when the app is hidden in the tray.

### Journal (Ctrl+0)
A page per zone: a short lore summary and **Tales of the zone**, the quest chains worth following there (their
steps, where they lead, the rewards). Filter Alliance / Horde. The Oracle also points out quest rewards in the
zone that would beat what your gear set wears.

### Map (Ctrl+M)
The game's own zone maps, fully explored. Scroll to zoom, drag to move, coordinates under the mouse like in game.
**View on map** buttons all over the app open the right zone with a marker.

### Oracle's Chronicle
The scroll at the bottom left: everything that changed this session (new game data, what the addon brought in)
and every message the app showed. It opens by itself when there's news.

## Keyboard shortcuts

| Keys | |
|---|---|
| **Ctrl+Shift+\_** | Hide to the tray / bring back (works from in game) |
| **/** | New search |
| **Ctrl+1 … Ctrl+9, Ctrl+0, Ctrl+M** | Items, Recipes, Crafting, Spells, Talents, Gear, Calculator, Loot, Notes, Journal, Map |
| **↑ ↓ · Tab · PgUp PgDn** | Move through any list, also while typing in its filter box |
| **Tab** in the search box | Take the filter suggestion (when the list shows one) |
| **Enter** | Open the highlighted result |
| **Shift+Enter / Shift+click** | Put the item in your gear set |
| **Ctrl+Enter / Ctrl+click** | Add its recipe to your craft list |
| **← at the start of the search text** | Step onto the filter chips (then ← → Del Enter) |
| **Hold left Alt** | Compare the item under the mouse with your gear set |
| **Esc** | Close the list / clear the search |

## Questions and troubleshooting

**The app doesn't find WoW.** Click **Browse…** in the bar above the search box and pick the *World of Warcraft*
folder (the one with `_forever_` inside).

**The addon is "out of date" in game.** Tick *Load out of date AddOns* in the AddOns list, and update the app:
new versions bring an updated addon and offer **Update addon**.

**Nothing comes in from the game.** The game only saves when you log out or `/reload`; `/oracle sync` does it on
demand. Check the Chronicle, and that the addon is ticked in the AddOns list.

**An item or quest is missing.** The app's data is updated whenever a new game build appears. New Forever content
fills in as you play with the addon installed.

**Start over.** Close the app and delete `%LOCALAPPDATA%\WowCompanion`. Keep `user.sqlite` from that folder if you
want to keep your gear sets, talent sets, notes and craft list.

**The WoW font.** The app uses the game's Friz Quadrata font when it finds your WoW install. If not, put the font
file in a folder named `font` next to the exe.

## Where the data comes from

- Items, spells, talents, maps and icons: the game's own files for WoW Forever (and Classic Era for a few missing
  items), via [wago.tools](https://wago.tools).
- Drops and drop chances, vendors, quests, NPCs: the [CMaNGOS](https://github.com/cmangos/classic-db) Classic
  database, topped up with what the addon sees in game.
- Zone lore: written for this app.

Everything is downloaded once and kept on your PC; only new game builds are fetched again.

## For developers

To build it yourself: Windows with the [.NET 10 SDK](https://dotnet.microsoft.com/download), then

```
git clone <this repository>
cd WoWOracle
dotnet run --project src\WowCompanion.Desktop
```

`.\publish.ps1` makes the same zip as the Releases page (in `dist\`). Building, releasing, the data pipeline, the addon's file formats and how the numbers are worked out:
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

The app's own code is MIT licensed ([LICENSE](LICENSE)). World of Warcraft names, data and art belong to Blizzard
Entertainment and are not covered by it.

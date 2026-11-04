==============================================================
                     W o W   O r a c l e
          A pocket companion for WoW Forever
==============================================================

GETTING STARTED
  1. Unzip anywhere (Desktop, Documents, a games folder...).
  2. Run "WoW Oracle.exe". No install, no .NET needed.
     Windows SmartScreen may warn because the exe isn't signed:
     click "More info" -> "Run anyway".
  3. The first start downloads the game data (items, spells,
     talents, item sources). Give it a minute or two. After that
     it only checks for new builds.

  Your data (talent sets, gear sets, craft list, icon cache) lives
  in %LOCALAPPDATA%\WowCompanion. Delete that folder to reset.

START MENU
  The app is portable: it stays in this folder and installs nothing.
  The bar above the search box offers "Add to Start menu" so Windows
  search finds it. Keep the folder somewhere fixed (not Downloads);
  if you move it, the shortcut follows on the next start.

THE IN-GAME ADDON (recommended)
  The zip has the WoW Oracle addon in the "addon" folder; keep it next
  to the exe. The app finds World of Warcraft by itself and shows a
  bar above the search box:
    "Install addon"  copies the addon into WoW. In game: /reload or
                     restart, and tick WoW Oracle in the AddOns list
                     if it's off.
    "Browse..."      if WoW isn't in a usual folder, pick the World of
                     Warcraft folder (the one with _classic_ or similar
                     inside).
  The "Addon" button at the bottom right shows the bar again any time
  (and offers updates when a newer addon comes with a new app version).

  After that it's automatic: your gear and talents (a gear set and a
  talent set named after your character), professions, known recipes
  and what's in your bags/bank reach the app when you log out or
  /reload. In game, /oracle sync sends it right away while the app is
  open. Other commands: /oracle (copy yourself), /oracle t (inspect your
  target), /oracle debug.

SHORTCUTS
  Ctrl+Shift+_     Hide to tray / bring back (works from in-game)
  /                Jump to search
  Ctrl+1 .. 9      Items, Recipes, Crafting, Spells, Talents,
                   Gear, Calculator, Loot, Notes
  Ctrl+0 / Ctrl+M  Journal / Map
  Shift+Enter      Put the item in your current gear set
  (or Shift+click)
  Ctrl+Enter       Add the item's recipe to your craft list
  (or Ctrl+click)
  Hold left Alt    Compare the item under the mouse with your gear set
  ?                (button in the search bar) all search filters

  Talents: "Share / import" has a box for Icy Veins and one for
  Wowhead. Copy the export link to share your build, or paste a
  friend's link into the matching box to get it as a new set.

  Search filters, e.g.:  neck stat:str   slot:2h lvl:40-50
                         type:mail q:epic class:paladin

  The X button exits. Use Ctrl+Shift+_ or the tray menu to keep it
  running in the background.

THE WOW FONT (optional)
  The app uses the game's own Friz Quadrata font if it finds your
  WoW install. If not, put a .ttf in a folder named "font" next to
  "WoW Oracle.exe".

CREDITS
  Game data: wago.tools (WoW Forever + Classic Era tables)
  Item sources: CMaNGOS classic-db
  Icons, maps: the game's own files, via wago.tools
  Not affiliated with Blizzard Entertainment. World of Warcraft
  is a trademark of Blizzard Entertainment.
==============================================================

ABOUT
  WoW Oracle is free and fan-made. It is not affiliated with or
  endorsed by Blizzard Entertainment. World of Warcraft is a
  trademark of Blizzard Entertainment, Inc. No game data or art
  comes in this zip: the app downloads it on your PC (wago.tools,
  CMaNGOS). See LICENSE.txt and THIRD-PARTY-NOTICES.md.

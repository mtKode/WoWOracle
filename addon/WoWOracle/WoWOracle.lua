--[[
  WoW Oracle: copy a character for the desktop app.

  Buttons: "Oracle" on the character frame (you) and on the inspect window (the inspected player).
  Commands: /oracle            copy yourself
            /oracle t          inspect your target and copy it (also: /oracle target)
            /oracle talents    save your class's talent tree for the app (also done at login/logout)
            /oracle quests     how many quests are saved for the app ("/oracle quests clear" empties the list)
            /oracle sync       save gear, talents, professions, recipes and bags now and reload, so the app gets them right away
            /oracle debug      print which game APIs this client has (talents, inspect, stats)

  The export is plain text between "!WOWORACLE" and "!END". The window it shows in has the text selected:
  press Ctrl+C, then paste into WoW Oracle (Gear page > Import character). Every copy is also kept in
  SavedVariables (WoWOracleDB), written when you log out or /reload.

  Talents: Forever uses trait trees, so the C_Traits route is tried first; the classic GetTalentInfo route is the
  fallback. Talents are exported by name and rank (the app matches by name), plus spell/node ids when known.
]]

local ADDON = ...
local FORMAT_VERSION = 1
local GOLD = "|cffd4af37"

-- ------------------------------------------------------------------------------------------------
-- Small helpers
-- ------------------------------------------------------------------------------------------------

local captureTalentLayout -- defined further down (talent layout capture)

local function pack(...) return { n = select("#", ...), ... } end

-- Calls f safely; returns its results, or nothing when f is missing or errors.
local function try(f, ...)
    if type(f) ~= "function" then return end
    local r = pack(pcall(f, ...))
    if r[1] then return unpack(r, 2, r.n) end
end

local function say(msg) DEFAULT_CHAT_FRAME:AddMessage(GOLD .. "WoW Oracle|r: " .. tostring(msg)) end

-- Keep exported values on one line and free of our separators.
local function clean(v)
    v = tostring(v == nil and "" or v)
    return (v:gsub("[\r\n|;=]", " "))
end

local function round(v, places)
    if type(v) ~= "number" then return v end
    local m = 10 ^ (places or 2)
    return math.floor(v * m + 0.5) / m
end

-- Newer clients moved item functions into C_Item; use whichever this client has.
local GetItemInfoAny = (C_Item and C_Item.GetItemInfo) or GetItemInfo

-- ------------------------------------------------------------------------------------------------
-- What the app already knows (Known.lua, written by the app): the addon only saves what's new to it.
-- ------------------------------------------------------------------------------------------------

local knownSets

-- "a-b,c,d-e" -> sorted lists of range starts and ends (searched by halving, so big lists stay small in memory).
local function parseRanges(text)
    local list = {}
    if type(text) == "string" then
        for part in text:gmatch("[^,]+") do
            local a, b = part:match("^%s*(%d+)%-(%d+)%s*$")
            a = tonumber(a) or tonumber(part)
            if a then list[#list + 1] = { a, tonumber(b) or a } end
        end
    end
    table.sort(list, function(x, y) return x[1] < y[1] end)
    local lo, hi = {}, {}
    for i, r in ipairs(list) do lo[i], hi[i] = r[1], r[2] end
    return { lo = lo, hi = hi }
end

local function inRanges(r, id)
    local l, h = 1, #r.lo
    while l <= h do
        local m = math.floor((l + h) / 2)
        if id < r.lo[m] then h = m - 1 elseif id > r.hi[m] then l = m + 1 else return true end
    end
    return false
end

-- kind: "npcs", "placed" (NPCs with a known spot), "objects", "quests", "vendors", "sourced" (items with a known source),
-- "items" (items the app has data for). Nil when the app hasn't
-- written Known.lua yet: then nothing counts as new and nothing is saved.
local function isKnown(kind, id)
    if type(WoWOracleKnown) ~= "table" or type(id) ~= "number" then return nil end
    knownSets = knownSets or {}
    if not knownSets[kind] then knownSets[kind] = parseRanges(WoWOracleKnown[kind]) end
    return inRanges(knownSets[kind], id)
end

local function hasKnownList() return type(WoWOracleKnown) == "table" end

local function knownSummary()
    if not hasKnownList() then return "no known-data list yet (the app writes Known.lua when it starts)" end
    local parts = {}
    for _, kind in ipairs({ "npcs", "placed", "objects", "quests", "vendors", "sourced", "items" }) do
        isKnown(kind, 0)
        parts[#parts + 1] = ("%s %d ranges"):format(kind, #knownSets[kind].lo)
    end
    return "known list (build " .. tostring(WoWOracleKnown.build or "?") .. "): " .. table.concat(parts, ", ")
end


local function spellName(id)
    if not id then return nil end
    if C_Spell and C_Spell.GetSpellName then return try(C_Spell.GetSpellName, id) end
    return (try(GetSpellInfo, id))
end

-- ------------------------------------------------------------------------------------------------
-- Gear: inventory slots 1-19 (same numbers the app uses)
-- ------------------------------------------------------------------------------------------------

local function readGear(unit)
    local gear, missing = {}, 0
    for slot = 1, 19 do
        local id = try(GetInventoryItemID, unit, slot)
        local link = try(GetInventoryItemLink, unit, slot)
        if id or link then
            local itemString = link and link:match("item:([%-%d:]+)") or tostring(id)
            local fields = { strsplit(":", itemString) }
            local itemId = tonumber(fields[1]) or id
            -- Modern item strings: ...:linkLevel(9):spec(10):upgrade(11):context(12):numBonusIDs(13):bonusIDs...
            -- Forever's random suffixes ("of Eluding") are bonus ids.
            local bonuses = {}
            local count = tonumber(fields[13]) or 0
            for i = 1, count do bonuses[#bonuses + 1] = tonumber(fields[13 + i]) end
            gear[#gear + 1] = {
                slot = slot,
                id = itemId,
                enchant = tonumber(fields[2]) or 0,
                suffix = tonumber(fields[7]) or 0, -- random property ("of the Eagle")
                name = link and link:match("%[(.-)%]") or "",
                raw = itemString,
                link = link,
                bonuses = bonuses,
            }
        elseif try(GetInventoryItemTexture, unit, slot) then
            missing = missing + 1 -- the slot has an item the client hasn't sent yet
        end
    end
    return gear, missing
end

-- ------------------------------------------------------------------------------------------------
-- Item data: what the client knows about an item, for items the app's data doesn't have yet
-- ------------------------------------------------------------------------------------------------

-- GetItemStats keys -> the app's item stat types (ItemSparse StatModifier_bonusStat numbers).
local STAT_TYPES = {
    ITEM_MOD_MANA_SHORT = 0, ITEM_MOD_HEALTH_SHORT = 1, ITEM_MOD_AGILITY_SHORT = 3, ITEM_MOD_STRENGTH_SHORT = 4,
    ITEM_MOD_INTELLECT_SHORT = 5, ITEM_MOD_SPIRIT_SHORT = 6, ITEM_MOD_STAMINA_SHORT = 7,
    ITEM_MOD_DEFENSE_SKILL_RATING_SHORT = 12, ITEM_MOD_DODGE_RATING_SHORT = 13, ITEM_MOD_PARRY_RATING_SHORT = 14,
    ITEM_MOD_BLOCK_RATING_SHORT = 15, ITEM_MOD_HIT_RATING_SHORT = 31, ITEM_MOD_CRIT_RATING_SHORT = 32,
    ITEM_MOD_HASTE_RATING_SHORT = 36, ITEM_MOD_ATTACK_POWER_SHORT = 38, ITEM_MOD_RANGED_ATTACK_POWER_SHORT = 39,
    ITEM_MOD_SPELL_HEALING_DONE_SHORT = 41, ITEM_MOD_SPELL_DAMAGE_DONE_SHORT = 42, ITEM_MOD_MANA_REGENERATION_SHORT = 43,
    ITEM_MOD_SPELL_POWER_SHORT = 45, ITEM_MOD_HEALTH_REGEN_SHORT = 46, ITEM_MOD_SPELL_PENETRATION_SHORT = 47,
    ITEM_MOD_BLOCK_VALUE_SHORT = 48,
    RESISTANCE1_NAME = 53, RESISTANCE2_NAME = 51, RESISTANCE3_NAME = 55, RESISTANCE4_NAME = 52,
    RESISTANCE5_NAME = 54, RESISTANCE6_NAME = 56,
}

local INV_TYPES = {
    INVTYPE_HEAD = 1, INVTYPE_NECK = 2, INVTYPE_SHOULDER = 3, INVTYPE_BODY = 4, INVTYPE_CHEST = 5, INVTYPE_WAIST = 6,
    INVTYPE_LEGS = 7, INVTYPE_FEET = 8, INVTYPE_WRIST = 9, INVTYPE_HAND = 10, INVTYPE_FINGER = 11, INVTYPE_TRINKET = 12,
    INVTYPE_WEAPON = 13, INVTYPE_SHIELD = 14, INVTYPE_RANGED = 15, INVTYPE_CLOAK = 16, INVTYPE_2HWEAPON = 17,
    INVTYPE_TABARD = 19, INVTYPE_ROBE = 20, INVTYPE_WEAPONMAINHAND = 21, INVTYPE_WEAPONOFFHAND = 22,
    INVTYPE_HOLDABLE = 23, INVTYPE_THROWN = 25, INVTYPE_RANGEDRIGHT = 26, INVTYPE_RELIC = 28,
}

local function itemStats(linkOrId)
    local get = (C_Item and C_Item.GetItemStats) or GetItemStats
    local raw = try(get, type(linkOrId) == "number" and ("item:" .. linkOrId) or linkOrId) or {}
    local out, armor = {}, 0
    for key, value in pairs(raw) do
        if key == "RESISTANCE0_NAME" then
            armor = value
        elseif STAT_TYPES[key] then
            out[STAT_TYPES[key]] = (out[STAT_TYPES[key]] or 0) + value
        end
    end
    return out, armor
end

local function statText(stats)
    local keys = {}
    for k in pairs(stats) do keys[#keys + 1] = k end
    table.sort(keys)
    local parts = {}
    for _, k in ipairs(keys) do
        if stats[k] ~= 0 then parts[#parts + 1] = k .. ":" .. round(stats[k], 1) end
    end
    return table.concat(parts, ",")
end

-- Weapon damage and speed only show in the tooltip (English client).
local scanTip
local function tooltipNumbers(id)
    if not scanTip then
        scanTip = CreateFrame("GameTooltip", "WoWOracleScanTip", nil, "GameTooltipTemplate")
        scanTip:SetOwner(WorldFrame, "ANCHOR_NONE")
    end
    scanTip:ClearLines()
    try(scanTip.SetHyperlink, scanTip, "item:" .. id)
    local low, high, speed
    for i = 1, scanTip:NumLines() do
        local left = _G["WoWOracleScanTipTextLeft" .. i]
        local right = _G["WoWOracleScanTipTextRight" .. i]
        local l = left and left:GetText() or ""
        local r = right and right:GetText() or ""
        local a, b = l:match("(%d+) %- (%d+) Damage")
        if a and not low then low, high = tonumber(a), tonumber(b) end
        local sp = r:match("Speed (%d+%.%d+)") or l:match("Speed (%d+%.%d+)")
        if sp and not speed then speed = tonumber(sp) end
    end
    return low or 0, high or 0, speed and math.floor(speed * 1000 + 0.5) or 0
end

-- One "itemdata=" line: id;quality;ilvl;reqLevel;class;subclass;invType;icon;bind;sell;armor;dmgMin;dmgMax;delayMs;stats;name
local function itemDataLine(id)
    local name, _, quality, ilvl, reqLevel, _, _, _, equipLoc, icon, sell, classID, subclassID, bind = try(GetItemInfoAny, id)
    if not name then return nil end
    local inv = (C_Item and C_Item.GetItemInventoryTypeByID and try(C_Item.GetItemInventoryTypeByID, id)) or INV_TYPES[equipLoc] or 0
    local stats, armor = itemStats(id)
    local low, high, delay = tooltipNumbers(id)
    return ("itemdata=%d;%d;%d;%d;%d;%d;%d;%s;%d;%d;%d;%d;%d;%d;%s;%s"):format(
        id, quality or 1, ilvl or 0, reqLevel or 0, classID or 0, subclassID or 0, inv, tostring(icon or 0), bind or 0,
        sell or 0, armor or 0, low, high, delay, statText(stats), clean(name))
end

-- Items the app's data doesn't have (Known.lua "items"): their client data is saved once, so the app can show them
-- (names, icons, stats). Seen in bags, the bank, profession windows, loot and vendors.
local waitingItems = {}

local function saveItem(id, line)
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.seen = WoWOracleDB.seen or {}
    WoWOracleDB.seen.items = WoWOracleDB.seen.items or {}
    WoWOracleDB.seen.items[id] = { text = "!WOWORACLE-ITEM 1\n" .. line .. "\n!END", time = time() }
end

local function noteItem(id)
    if type(id) ~= "number" or isKnown("items", id) ~= false then return end
    local saved = WoWOracleDB and WoWOracleDB.seen and WoWOracleDB.seen.items
    if saved and saved[id] then return end
    local ok, line = pcall(itemDataLine, id) -- never let item data break the capture that called this
    if ok and line then saveItem(id, line); return end
    -- Not in the client's cache yet: asking loads it; try again in a moment.
    if waitingItems[id] then return end
    waitingItems[id] = true
    C_Timer.After(3, function()
        waitingItems[id] = nil
        local done, later = pcall(itemDataLine, id)
        if done and later then saveItem(id, later) end
    end)
end

-- Stats the equipped copy has beyond the plain item (random suffix like "of the Beast").
local function bonusLine(g)
    if not g.link then return nil end
    local base = itemStats(g.id)
    local actual = itemStats(g.link)
    local diff, any = {}, false
    for k, v in pairs(actual) do
        local d = v - (base[k] or 0)
        if d ~= 0 then diff[k] = d; any = true end
    end
    for k, v in pairs(base) do
        if actual[k] == nil and v ~= 0 then diff[k] = -v; any = true end
    end
    if not any then return nil end
    return ("bonus=%d;%s"):format(g.slot, statText(diff))
end

-- Items the client hasn't loaded yet have no name or stats; ask for them and wait a moment.
local function whenItemsLoaded(unit, done)
    local tries = 0
    local safeCheck
    local function check()
        local pending = 0
        for slot = 1, 19 do
            local id = try(GetInventoryItemID, unit, slot)
            if id and not try(GetItemInfoAny, id) then
                pending = pending + 1
                if C_Item and C_Item.RequestLoadItemDataByID then try(C_Item.RequestLoadItemDataByID, id) end
            end
        end
        tries = tries + 1
        if pending == 0 or tries >= 10 then done() else C_Timer.After(0.2, safeCheck) end
    end
    safeCheck = function()
        local ok, err = pcall(check)
        if not ok then say("|cffff6060export failed:|r " .. tostring(err)) end
    end
    safeCheck()
end

-- ------------------------------------------------------------------------------------------------
-- Talents
-- ------------------------------------------------------------------------------------------------

-- Trait trees (C_Traits). configID: your active config, or the inspect config.
local function traitTalents(configID)
    if not (configID and C_Traits and C_Traits.GetConfigInfo) then return nil end
    local config = try(C_Traits.GetConfigInfo, configID)
    if not (config and config.treeIDs and #config.treeIDs > 0) then return nil end

    local out = {}
    for _, treeID in ipairs(config.treeIDs) do
        for _, nodeID in ipairs(try(C_Traits.GetTreeNodes, treeID) or {}) do
            local node = try(C_Traits.GetNodeInfo, configID, nodeID)
            local rank = node and (node.activeRank or node.currentRank) or 0
            if rank > 0 then
                local entryID = (node.activeEntry and node.activeEntry.entryID) or (node.entryIDs and node.entryIDs[1])
                local entry = entryID and try(C_Traits.GetEntryInfo, configID, entryID)
                local def = entry and entry.definitionID and try(C_Traits.GetDefinitionInfo, entry.definitionID)
                local spellID = def and (def.spellID or def.overriddenSpellID)
                local name = (def and def.overrideName ~= "" and def.overrideName) or spellName(spellID)
                out[#out + 1] = { name = name or ("node " .. nodeID), rank = rank, node = nodeID, spell = spellID }
            end
        end
    end
    return out
end

-- Classic talent frames (GetTalentInfo). isInspect: read the inspected unit.
local function classicTalents(isInspect)
    local tabs = try(GetNumTalentTabs, isInspect) or 0
    if tabs == 0 then return nil end
    local out = {}
    for tab = 1, tabs do
        for i = 1, (try(GetNumTalents, tab, isInspect) or 0) do
            local name, _, tier, column, rank = try(GetTalentInfo, tab, i, isInspect)
            if name and rank and rank > 0 then
                out[#out + 1] = { name = name, rank = rank, tab = tab, tier = tier, col = column }
            end
        end
    end
    return out
end

local function readTalents(isInspect)
    local configID
    if isInspect then
        configID = Constants and Constants.TraitConsts and Constants.TraitConsts.INSPECT_TRAIT_CONFIG_ID
    elseif C_ClassTalents and C_ClassTalents.GetActiveConfigID then
        configID = try(C_ClassTalents.GetActiveConfigID)
    end
    local traits = traitTalents(configID)
    if traits and #traits > 0 then return traits, "traits" end
    local classic = classicTalents(isInspect)
    if classic then return classic, "classic" end
    return {}, "none"
end

-- ------------------------------------------------------------------------------------------------
-- Stats (only for yourself; the game doesn't send other players' character sheets)
-- ------------------------------------------------------------------------------------------------

local function readStats()
    local s = {}
    local names = { "str", "agi", "sta", "int", "spi" }
    for i = 1, 5 do
        local base, total = try(UnitStat, "player", i)
        s[names[i]] = total
        s[names[i] .. "_base"] = base
    end
    s.hp = try(UnitHealthMax, "player")
    s.mana = try(UnitPowerMax, "player", 0)
    local _, armor = try(UnitArmor, "player")
    s.armor = armor
    local apBase, apPos, apNeg = try(UnitAttackPower, "player")
    if apBase then s.ap = apBase + (apPos or 0) + (apNeg or 0) end
    local rapBase, rapPos, rapNeg = try(UnitRangedAttackPower, "player")
    if rapBase then s.rap = rapBase + (rapPos or 0) + (rapNeg or 0) end
    s.crit = round(try(GetCritChance))
    s.rcrit = round(try(GetRangedCritChance))
    s.hit = round(try(GetHitModifier))
    s.spellhit = round(try(GetSpellHitModifier))
    local sp, scrit = 0, 0
    for school = 2, 7 do
        sp = math.max(sp, try(GetSpellBonusDamage, school) or 0)
        scrit = math.max(scrit, try(GetSpellCritChance, school) or 0)
    end
    s.sp = sp
    s.scrit = round(scrit)
    s.heal = try(GetSpellBonusHealing)
    s.dodge = round(try(GetDodgeChance))
    s.parry = round(try(GetParryChance))
    s.block = round(try(GetBlockChance))
    s.mp5 = try(GetManaRegen) and round((select(2, GetManaRegen())) * 5, 0) or nil
    return s
end

-- ------------------------------------------------------------------------------------------------
-- Export text
-- ------------------------------------------------------------------------------------------------

local function buildExport(unit, isInspect)
    -- Forever characters have a surname ("Varnehan Burntoe"); UnitName returns it as the second value,
    -- where other clients put the realm. Keep both words as the name, and take the realm from the client.
    local first, second = UnitName(unit)
    local name = (second and second ~= "") and (first .. " " .. second) or first
    local realm = GetRealmName()
    local className, classFile, classID = UnitClass(unit)
    local raceName, raceFile, raceID = UnitRace(unit)
    local gear, missing = readGear(unit)
    local talents, talentSource = readTalents(isInspect)
    local _, _, _, interface = GetBuildInfo()

    local lines = {
        "!WOWORACLE " .. FORMAT_VERSION,
        "name=" .. clean(name),
        "realm=" .. clean(realm),
        "level=" .. (UnitLevel(unit) or 0),
        "class=" .. clean(classFile) .. ";" .. (classID or 0) .. ";" .. clean(className),
        "race=" .. clean(raceFile) .. ";" .. (raceID or 0) .. ";" .. clean(raceName),
        "sex=" .. (UnitSex(unit) or 0),
        "guild=" .. clean((GetGuildInfo(unit))),
        "source=" .. (isInspect and "inspect" or "self"),
        "client=" .. clean(interface),
        "time=" .. time(),
        "talents=" .. talentSource,
    }
    for _, g in ipairs(gear) do
        lines[#lines + 1] = ("item=%d;%d;%d;%d;%s;%s;%s"):format(g.slot, g.id or 0, g.enchant, g.suffix, clean(g.name),
            clean(g.raw), table.concat(g.bonuses, ","))
        local bonus = bonusLine(g)
        if bonus then lines[#lines + 1] = bonus end
    end
    -- Full item data, so the app can learn items its game data doesn't have yet (newer client build).
    local done = {}
    for _, g in ipairs(gear) do
        if g.id and not done[g.id] then
            done[g.id] = true
            local data = itemDataLine(g.id)
            if data then lines[#lines + 1] = data end
        end
    end
    for _, t in ipairs(talents) do
        lines[#lines + 1] = ("talent=%s;%d;%s;%s"):format(clean(t.name), t.rank, t.spell or "", t.node or (t.tab and (t.tab .. "/" .. t.tier .. "/" .. t.col)) or "")
    end
    if not isInspect then
        local stats = readStats()
        local keys = {}
        for key in pairs(stats) do keys[#keys + 1] = key end
        table.sort(keys)
        for _, key in ipairs(keys) do lines[#lines + 1] = "stat=" .. key .. ";" .. tostring(stats[key]) end
    end
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n"), #gear, #talents, missing, name
end

-- ------------------------------------------------------------------------------------------------
-- Copy window: text pre-selected, Ctrl+C closes it
-- ------------------------------------------------------------------------------------------------

local copyFrame
local function showCopy(text, title)
    if not copyFrame then
        local f = CreateFrame("Frame", "WoWOracleCopyFrame", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
        f:SetSize(460, 320)
        f:SetPoint("CENTER")
        f:SetFrameStrata("DIALOG")
        f:EnableMouse(true)
        f:SetMovable(true)
        f:RegisterForDrag("LeftButton")
        f:SetScript("OnDragStart", f.StartMoving)
        f:SetScript("OnDragStop", f.StopMovingOrSizing)
        if f.SetBackdrop then
            f:SetBackdrop({
                bgFile = "Interface\\DialogFrame\\UI-DialogBox-Background-Dark",
                edgeFile = "Interface\\DialogFrame\\UI-DialogBox-Gold-Border",
                tile = true, tileSize = 32, edgeSize = 24,
                insets = { left = 6, right = 6, top = 6, bottom = 6 },
            })
        end
        tinsert(UISpecialFrames, "WoWOracleCopyFrame") -- Esc closes it

        f.title = f:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
        f.title:SetPoint("TOP", 0, -16)
        f.hint = f:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
        f.hint:SetPoint("TOP", f.title, "BOTTOM", 0, -6)
        f.hint:SetText("Press Ctrl+C, then paste in WoW Oracle: Gear > Import character")

        local close = CreateFrame("Button", nil, f, "UIPanelCloseButton")
        close:SetPoint("TOPRIGHT", -4, -4)

        local scroll = CreateFrame("ScrollFrame", "WoWOracleCopyScroll", f, "UIPanelScrollFrameTemplate")
        scroll:SetPoint("TOPLEFT", 18, -58)
        scroll:SetPoint("BOTTOMRIGHT", -36, 18)
        local edit = CreateFrame("EditBox", nil, scroll)
        edit:SetMultiLine(true)
        edit:SetAutoFocus(true)
        edit:SetFontObject(ChatFontNormal)
        edit:SetWidth(400)
        edit:SetScript("OnEscapePressed", function() f:Hide() end)
        -- Typing would change the export; put the text back instead.
        edit:SetScript("OnTextChanged", function(self, user) if user then self:SetText(f.text or ""); self:HighlightText() end end)
        edit:SetScript("OnKeyDown", function(_, key)
            if key == "C" and IsControlKeyDown() then C_Timer.After(0.1, function() f:Hide(); say("copied.") end) end
        end)
        scroll:SetScrollChild(edit)
        f.edit = edit
        copyFrame = f
    end
    copyFrame.text = text
    copyFrame.title:SetText(title)
    copyFrame.edit:SetText(text)
    copyFrame:Show()
    copyFrame.edit:SetFocus()
    copyFrame.edit:HighlightText()
end

-- ------------------------------------------------------------------------------------------------
-- Copy yourself / an inspected unit
-- ------------------------------------------------------------------------------------------------

local function remember(name, text)
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.captures = WoWOracleDB.captures or {}
    WoWOracleDB.captures[name or "?"] = { time = time(), text = text }
end

local function export(unit, isInspect)
    whenItemsLoaded(unit, function()
        local ok, text, items, talents, missing, name = pcall(buildExport, unit, isInspect)
        if not ok then return say("|cffff6060export failed:|r " .. tostring(text) .. " (please send this line)") end
        remember(name, text)
        local title = ("%s: %d items, %d talents"):format(name or "?", items, talents)
        if missing > 0 then title = title .. (" (%d not loaded)"):format(missing) end
        showCopy(text, title)
        if isInspect then pcall(captureTalentLayout, true, unit) end -- their class's tree, for the app
    end)
end

function WoWOracle_CopySelf() export("player", false) end

-- Inspect data arrives with INSPECT_READY; wait for it (max 2 s) so items and talents are complete.
local waiting
local waiter = CreateFrame("Frame")
waiter:SetScript("OnEvent", function(self, _, guid)
    if waiting and (not guid or guid == UnitGUID(waiting)) then
        self:UnregisterEvent("INSPECT_READY")
        local unit = waiting
        waiting = nil
        C_Timer.After(0.2, function() export(unit, true) end)
    end
end)

function WoWOracle_CopyInspect(unit)
    unit = unit or (InspectFrame and InspectFrame.unit) or "target"
    if not UnitExists(unit) or not UnitIsPlayer(unit) then return say("no player to inspect.") end
    if UnitIsUnit(unit, "player") then return WoWOracle_CopySelf() end
    if not try(CanInspect, unit) then return say("can't inspect " .. (UnitName(unit) or "that") .. " (too far away?).") end
    waiting = unit
    waiter:RegisterEvent("INSPECT_READY")
    NotifyInspect(unit)
    C_Timer.After(2, function()
        if waiting == unit then
            waiting = nil
            waiter:UnregisterEvent("INSPECT_READY")
            export(unit, true) -- copy what we have
        end
    end)
end

-- ------------------------------------------------------------------------------------------------
-- Buttons
-- ------------------------------------------------------------------------------------------------

local function makeButton(parent, onClick, tooltip)
    local b = CreateFrame("Button", nil, parent, "UIPanelButtonTemplate")
    b:SetSize(64, 18)
    b:SetText("Oracle")
    b:SetScript("OnClick", onClick)
    b:SetScript("OnEnter", function(self)
        GameTooltip:SetOwner(self, "ANCHOR_RIGHT")
        GameTooltip:AddLine("WoW Oracle", 0.83, 0.69, 0.22)
        GameTooltip:AddLine(tooltip, 1, 1, 1, true)
        GameTooltip:Show()
    end)
    b:SetScript("OnLeave", GameTooltip_Hide)
    return b
end

-- Bottom left of the paper doll: just left of the weapon slots, where the frame has free space.
-- Falls back to the frame's bottom-left corner when the slot isn't there.
local function placeBottomLeft(button, mainHandSlot, frame)
    button:ClearAllPoints()
    if mainHandSlot then
        -- Live in the same frame as the weapon slots and draw above the character model,
        -- which otherwise covers this corner.
        button:SetParent(mainHandSlot:GetParent() or frame)
        button:SetFrameStrata(mainHandSlot:GetFrameStrata())
        button:SetFrameLevel(mainHandSlot:GetFrameLevel() + 5)
        -- In the free corner under the left slot column (below the bottom slot, the wrist).
        button:SetSize(70, 20)
        local wrist = _G[(mainHandSlot:GetName() or ""):gsub("MainHandSlot$", "WristSlot")]
        if wrist then
            button:SetPoint("TOPLEFT", wrist, "BOTTOMLEFT", -6, -16)
        else
            button:SetPoint("RIGHT", mainHandSlot, "LEFT", -6, 0)
        end
    else
        button:SetFrameLevel(frame:GetFrameLevel() + 20)
        button:SetPoint("BOTTOMLEFT", frame, "BOTTOMLEFT", 18, 16)
    end
    button:Show()
end

local function addInspectButton()
    if not InspectFrame or WoWOracle_InspectButton then return end
    local parent = InspectPaperDollFrame or InspectFrame
    local b = makeButton(parent, function() WoWOracle_CopyInspect(InspectFrame.unit) end,
        "Copy this player's gear and talents for the app.")
    placeBottomLeft(b, InspectMainHandSlot, InspectFrame)
    WoWOracle_InspectButton = b
end

local function addCharacterButton()
    if not CharacterFrame or WoWOracle_CharacterButton then return end
    local parent = PaperDollFrame or CharacterFrame
    local b = makeButton(parent, WoWOracle_CopySelf, "Copy your gear, talents and stats for the app.")
    placeBottomLeft(b, CharacterMainHandSlot, CharacterFrame)
    WoWOracle_CharacterButton = b
end

-- ------------------------------------------------------------------------------------------------
-- Debug: what this client offers, so the exporter can be tuned to Forever
-- ------------------------------------------------------------------------------------------------

local function debug()
    local version, build, date, interface = GetBuildInfo()
    say(("client %s (%s) %s, interface %s"):format(version, build, date, interface))
    local function has(path, value) say(("%s: %s"):format(path, value ~= nil and "yes" or "no")) end
    has("C_Traits", C_Traits)
    has("C_ClassTalents", C_ClassTalents)
    has("INSPECT_TRAIT_CONFIG_ID", Constants and Constants.TraitConsts and Constants.TraitConsts.INSPECT_TRAIT_CONFIG_ID)
    has("GetTalentInfo", GetTalentInfo)
    has("C_Traits.GenerateInspectImportString", C_Traits and C_Traits.GenerateInspectImportString)
    say("talent tabs (you): " .. tostring((try(GetNumTalentTabs, false))))
    say("C_Item.GetItemInfo: " .. (C_Item and C_Item.GetItemInfo and "yes" or "no") .. ", GetItemInfo: " .. (GetItemInfo and "yes" or "no"))
    local configID = C_ClassTalents and try(C_ClassTalents.GetActiveConfigID)
    say("active trait config: " .. tostring(configID))
    local traits = traitTalents(configID)
    say("trait talents (you): " .. (traits and #traits or "n/a"))
    local classic = classicTalents(false)
    say("classic talents (you): " .. (classic and #classic or "n/a"))
    local list = (traits and #traits > 0 and traits) or classic or {}
    for i = 1, math.min(5, #list) do say(("  %s %d"):format(tostring(list[i].name), list[i].rank)) end
    local gear = readGear("player")
    say("items equipped: " .. #gear)
    say("talent layout nodes: " .. tostring(captureTalentLayout(true)))
    -- Professions and bags (crafting planner sync): which routes this client has.
    has("GetNumSkillLines + GetSkillLineInfo", GetNumSkillLines and GetSkillLineInfo)
    has("GetProfessions + GetProfessionInfo", GetProfessions and GetProfessionInfo)
    has("GetNumTradeSkills + GetTradeSkillRecipeLink", GetNumTradeSkills and GetTradeSkillRecipeLink)
    has("GetNumCrafts + GetCraftRecipeLink (enchanting)", GetNumCrafts and (GetCraftRecipeLink or GetCraftItemLink))
    has("C_TradeSkillUI.GetAllRecipeIDs", C_TradeSkillUI and C_TradeSkillUI.GetAllRecipeIDs)
    has("C_Container.GetContainerItemInfo", C_Container and C_Container.GetContainerItemInfo)
    has("GetContainerItemInfo", GetContainerItemInfo)
    local skills = {}
    for i = 1, (try(GetNumSkillLines)) or 0 do
        local name, isHeader, _, rank, _, _, maxRank = try(GetSkillLineInfo, i)
        if name and not isHeader and (maxRank or 0) > 1 then skills[#skills + 1] = ("%s %d/%d"):format(name, rank or 0, maxRank or 0) end
    end
    say("skills: " .. (#skills > 0 and table.concat(skills, ", ") or "none read"))
    local flavor = WOW_PROJECT_ID and tostring(WOW_PROJECT_ID) or "?"
    say(knownSummary())
    say("project id: " .. flavor .. " · saved characters: " .. tostring(WoWOracleDB and WoWOracleDB.characters and next(WoWOracleDB.characters) ~= nil))
end

-- ------------------------------------------------------------------------------------------------
-- Talent layout capture: the live talent tree of your class, saved for the app (SavedVariables).
-- The app uses it when the client is a newer build than its downloaded game data (wago.tools stays the
-- master once it catches up). One line per talent node:
--   node=id;posX;posY;maxRanks;spellId;icon;unlocks(node ids, comma separated);name;description
-- ------------------------------------------------------------------------------------------------

local function spellText(id)
    if C_Spell and C_Spell.GetSpellDescription then return try(C_Spell.GetSpellDescription, id) or "" end
    return try(GetSpellDescription, id) or ""
end

local function spellIcon(id)
    if C_Spell and C_Spell.GetSpellTexture then return try(C_Spell.GetSpellTexture, id) or 0 end
    return try(GetSpellTexture, id) or 0
end

-- unit: "player" (default) or an inspected unit (its tree is read through the inspect config).
function captureTalentLayout(quiet, unit) -- assigns the local declared at the top
    unit = unit or "player"
    if not (C_Traits and C_Traits.GetConfigInfo) then return 0 end
    local configID
    if unit == "player" then
        configID = C_ClassTalents and C_ClassTalents.GetActiveConfigID and try(C_ClassTalents.GetActiveConfigID)
    else
        configID = Constants and Constants.TraitConsts and Constants.TraitConsts.INSPECT_TRAIT_CONFIG_ID
    end
    local config = configID and try(C_Traits.GetConfigInfo, configID)
    local treeID = config and config.treeIDs and config.treeIDs[1]
    if not treeID then return 0 end

    local _, classFile, classID = UnitClass(unit)
    if not classFile then return 0 end
    local _, build, _, interface = GetBuildInfo()
    local lines = {
        "!WOWORACLE-TALENTS 1",
        "class=" .. clean(classFile) .. ";" .. (classID or 0),
        "build=" .. clean(build),
        "client=" .. clean(interface),
        "tree=" .. treeID,
        "time=" .. time(),
    }
    local count = 0
    for _, nodeID in ipairs(try(C_Traits.GetTreeNodes, treeID) or {}) do
        local node = try(C_Traits.GetNodeInfo, configID, nodeID)
        if node and node.ID and node.ID ~= 0 and node.isVisible ~= false and (node.maxRanks or 0) > 0 then
            local entryID = (node.activeEntry and node.activeEntry.entryID) or (node.entryIDs and node.entryIDs[1])
            local entry = entryID and try(C_Traits.GetEntryInfo, configID, entryID)
            local def = entry and entry.definitionID and try(C_Traits.GetDefinitionInfo, entry.definitionID)
            local spellID = def and (def.spellID or def.overriddenSpellID)
            if spellID then
                local name = (def.overrideName and def.overrideName ~= "" and def.overrideName) or spellName(spellID) or ""
                local unlocks = {}
                for _, edge in ipairs(node.visibleEdges or {}) do
                    if edge.type == 2 or edge.type == 3 then unlocks[#unlocks + 1] = edge.targetNode end
                end
                lines[#lines + 1] = ("node=%d;%d;%d;%d;%d;%s;%s;%s;%s"):format(
                    nodeID, node.posX or 0, node.posY or 0, node.maxRanks or 1, spellID, tostring(spellIcon(spellID)),
                    table.concat(unlocks, ","), clean(name), clean(spellText(spellID)))
                count = count + 1
            end
        end
    end
    lines[#lines + 1] = "!END"
    if count > 0 then
        WoWOracleDB = WoWOracleDB or {}
        WoWOracleDB.talentLayouts = WoWOracleDB.talentLayouts or {}
        local old = WoWOracleDB.talentLayouts[classFile]
        if unit ~= "player" and old then
            -- An inspect only replaces a saved tree when it's at least as complete (same or newer client).
            local _, oldCount = old:gsub("\nnode=", "")
            local oldBuild = tonumber(old:match("\nbuild=(%d+)")) or 0
            if oldCount > count or oldBuild > (tonumber(build) or 0) then return count, false end
        end
        WoWOracleDB.talentLayouts[classFile] = table.concat(lines, "\n")
        if unit ~= "player" and not old then say(("saved the %s talent tree from %s."):format(classFile, UnitName(unit) or "?")) end
        if not quiet then say(("saved the %s talent layout (%d talents); the app reads it after /reload or logout."):format(classFile, count)) end
    end
    return count, true
end

-- ------------------------------------------------------------------------------------------------
-- Quests: every quest you're offered, accept or hand in is saved once (WoWOracleDB.quests[id]) with its
-- giver, where it was picked up and its rewards. The app reads them on start, keeps them in its own database
-- and adds the ones its data doesn't have. Nothing is synced back; "/oracle quests clear" empties the list.
-- ------------------------------------------------------------------------------------------------

local function questStore()
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.quests = WoWOracleDB.quests or {}
    return WoWOracleDB.quests
end

-- The zone (or dungeon) map you're on, skipping caves and other sub maps: name, x, y (0-100).
local function playerPlace()
    local fallback = (GetZoneText and try(GetZoneText)) or ""
    if not (C_Map and C_Map.GetBestMapForUnit) then return fallback, 0, 0 end
    local mapID = try(C_Map.GetBestMapForUnit, "player")
    local info = mapID and try(C_Map.GetMapInfo, mapID)
    while info and (info.mapType or 0) > 4 and (info.parentMapID or 0) > 0 do
        mapID = info.parentMapID
        info = try(C_Map.GetMapInfo, mapID)
    end
    local x, y = 0, 0
    local pos = mapID and try(C_Map.GetPlayerMapPosition, mapID, "player")
    if pos and pos.GetXY then x, y = pos:GetXY() end
    return (info and info.name) or fallback, round((x or 0) * 100, 1), round((y or 0) * 100, 1)
end

-- Who you're talking to: kind ("npc"/"object"/""), id, name.
local function questGiver()
    local guid = UnitGUID("npc")
    local name = UnitName("npc") or ""
    if not guid then return "", 0, name end
    local kind, _, _, _, _, id = strsplit("-", guid)
    if kind == "Creature" or kind == "Vehicle" then return "npc", tonumber(id) or 0, name end
    if kind == "GameObject" then return "object", tonumber(id) or 0, name end
    return "", 0, "" -- started from an item (the "npc" is you)
end

-- Quest log rows: title, level, isHeader, questID (modern C_QuestLog first, classic API as fallback).
local function logRow(i)
    if C_QuestLog and C_QuestLog.GetInfo then
        local info = try(C_QuestLog.GetInfo, i)
        if info then return info.title, info.level, info.isHeader, info.questID end
        return
    end
    local title, level, _, isHeader, _, _, _, questID = try(GetQuestLogTitle, i)
    return title, level, isHeader, questID
end

local function logCount()
    if C_QuestLog and C_QuestLog.GetNumQuestLogEntries then return (try(C_QuestLog.GetNumQuestLogEntries)) or 0 end
    return (try(GetNumQuestLogEntries)) or 0
end

-- Level, zone header (the quest log groups quests by zone or dungeon) and log index of a quest.
local function logInfo(questID)
    local header
    for i = 1, logCount() do
        local title, level, isHeader, id = logRow(i)
        if isHeader then header = title
        elseif id == questID then return level, header, i end
    end
end

local function itemIdOf(link) return link and tonumber(link:match("item:(%d+)")) end

-- Rewards shown in the quest window (offer or hand-in).
local function frameRewards()
    local rewards, choices, data = {}, {}, {}
    local function grab(kind, count, list)
        for i = 1, count or 0 do
            local id = itemIdOf(try(GetQuestItemLink, kind, i))
            if id then list[#list + 1] = id; data[#data + 1] = try(itemDataLine, id) end
        end
    end
    grab("reward", try(GetNumQuestRewards), rewards)
    grab("choice", try(GetNumQuestChoices), choices)
    return rewards, choices, data, (try(GetRewardMoney)) or 0, (try(GetRewardXP)) or 0
end

-- Rewards of a quest already in your log (for quests picked up before the addon was installed).
-- Classic selects by log index, newer clients by quest id; the quest id is passed along for clients that take it.
local function logRewards(index, questID)
    local previous
    if C_QuestLog and C_QuestLog.SetSelectedQuest then
        previous = C_QuestLog.GetSelectedQuest and try(C_QuestLog.GetSelectedQuest)
        try(C_QuestLog.SetSelectedQuest, questID)
    elseif SelectQuestLogEntry then
        previous = try(GetQuestLogSelection)
        try(SelectQuestLogEntry, index)
    end
    local rewards, choices, data = {}, {}, {}
    local function grab(kind, count, list)
        for i = 1, count or 0 do
            local id = itemIdOf(try(GetQuestLogItemLink, kind, i, questID))
            if id then list[#list + 1] = id; data[#data + 1] = try(itemDataLine, id) end
        end
    end
    grab("reward", try(GetNumQuestLogRewards, questID), rewards)
    grab("choice", try(GetNumQuestLogChoices, questID, true), choices)
    local money = (try(GetQuestLogRewardMoney, questID)) or 0
    if previous and previous ~= 0 then
        if C_QuestLog and C_QuestLog.SetSelectedQuest then try(C_QuestLog.SetSelectedQuest, previous)
        else try(SelectQuestLogEntry, previous) end
    end
    return rewards, choices, data, money
end

local function questText(q)
    local _, build = GetBuildInfo()
    local lines = {
        "!WOWORACLE-QUEST 1",
        "id=" .. q.id,
        "title=" .. clean(q.title),
        "level=" .. (q.level or 0),
        "zone=" .. clean(q.zone),
        ("giver=%s;%d;%s"):format(q.giverKind or "", q.giverId or 0, clean(q.giverName)),
        ("at=%s;%s;%s"):format(clean(q.at), tostring(q.x or 0), tostring(q.y or 0)),
        "reward=" .. table.concat(q.rewards or {}, ","),
        "choice=" .. table.concat(q.choices or {}, ","),
        "money=" .. (q.money or 0),
        "xp=" .. (q.xp or 0),
        "build=" .. clean(build),
        "time=" .. (q.time or time()),
    }
    for _, line in ipairs(q.items or {}) do lines[#lines + 1] = line end
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n")
end

-- Fills in what's missing for a quest (never overwrites known values with blanks) and refreshes its text.
local function saveQuest(id, fields)
    if not id or id == 0 then return end
    local store = questStore()
    local q = store[id] or { id = id, time = time() }
    for k, v in pairs(fields) do
        local blank = v == nil or v == "" or v == 0 or (type(v) == "table" and #v == 0)
        if not blank then q[k] = v end
    end
    store[id] = q
    q.text = questText(q)
    return q
end

local function onQuestDetail()
    local id = try(GetQuestID)
    if not id or id == 0 or isKnown("quests", id) ~= false then return end -- only quests the app doesn't know
    local q = questStore()[id]
    if q and q.full then return end -- seen it already
    local kind, giverId, giverName = questGiver()
    local at, x, y = playerPlace()
    local rewards, choices, items, money, xp = frameRewards()
    saveQuest(id, {
        title = try(GetTitleText), giverKind = kind, giverId = giverId, giverName = giverName,
        at = at, x = x, y = y, rewards = rewards, choices = choices, items = items, money = money, xp = xp,
        full = kind ~= "" or giverName == "", -- giver known (or an item quest): nothing more to learn at pick-up
        rewardsSeen = true,
    })
end

local function onQuestAccepted(a, b)
    local id = b or a -- classic: (logIndex, questID); modern: (questID)
    if not id or isKnown("quests", id) ~= false then return end
    local level, zone = logInfo(id)
    saveQuest(id, { level = level, zone = zone })
end

local function onQuestComplete()
    local id = try(GetQuestID)
    if not id or id == 0 or isKnown("quests", id) ~= false then return end
    local q = questStore()[id]
    if q and q.rewardsSeen and q.level then return end
    local level, zone = logInfo(id)
    local rewards, choices, items, money, xp = frameRewards()
    saveQuest(id, {
        title = try(GetTitleText), level = level, zone = zone,
        rewards = rewards, choices = choices, items = items, money = money, xp = xp, rewardsSeen = true,
    })
end

-- Quests already in the log that we've never seen (picked up before the addon): title, level, zone, rewards.
local function scanQuestLog()
    local store = questStore()
    local header
    for i = 1, logCount() do
        local title, level, isHeader, id = logRow(i)
        if isHeader then
            header = title
        elseif id and id > 0 and isKnown("quests", id) == false and not (store[id] and store[id].rewardsSeen) then
            local rewards, choices, items, money = logRewards(i, id)
            saveQuest(id, { title = title, level = level, zone = header, rewards = rewards, choices = choices, items = items, money = money,
                rewardsSeen = (#rewards + #choices) > 0 or nil })
        end
    end
end

local function questCount()
    local n = 0
    for _ in pairs(questStore()) do n = n + 1 end
    return n
end

-- ------------------------------------------------------------------------------------------------
-- Character snapshot for the app (crafting planner): profession ranks, known recipes, and what you carry
-- (bags, and the bank as last seen). Saved on its own: at login, when skills or bags change, at logout.
-- "/oracle sync" saves and reloads the UI, so the game writes the file and the app picks it up right away.
-- ------------------------------------------------------------------------------------------------

local charState = { skills = {}, recipes = {}, bags = {}, bank = nil }

local function charKey()
    local name, surname = UnitName("player")
    return name .. ((surname and surname ~= "") and (" " .. surname) or "")
end

local function charStore()
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.characters = WoWOracleDB.characters or {}
    return WoWOracleDB.characters
end

-- Profession ranks: the classic skill list first, newer profession API as fallback.
local function readSkills()
    local skills = {}
    local count = (try(GetNumSkillLines)) or 0
    for i = 1, count do
        local name, isHeader, _, rank, _, _, maxRank = try(GetSkillLineInfo, i)
        if name and not isHeader and (maxRank or 0) > 1 then skills[name] = { rank or 0, maxRank or 0 } end
    end
    if next(skills) == nil and GetProfessions then
        local list = pack(try(GetProfessions)) -- may have gaps (no second profession, no fishing…)
        for i = 1, list.n do
            local index = list[i]
            if index then
                local name, _, rank, maxRank = try(GetProfessionInfo, index)
                if name then skills[name] = { rank or 0, maxRank or 0 } end
            end
        end
    end
    return skills
end

local function spellIdOfLink(link)
    if type(link) ~= "string" then return nil end
    return tonumber(link:match("enchant:(%d+)")) or tonumber(link:match("spell:(%d+)"))
end

-- Recipes in the open profession window (tradeskill, or the craft window enchanting uses in Classic).
local function readOpenRecipes()
    local found, profession = {}, nil
    if GetNumTradeSkills and (try(GetNumTradeSkills) or 0) > 0 then
        profession = try(GetTradeSkillLine)
        for i = 1, (try(GetNumTradeSkills)) or 0 do
            local _, kind = try(GetTradeSkillInfo, i)
            if kind and kind ~= "header" and kind ~= "subheader" then
                local id = spellIdOfLink(try(GetTradeSkillRecipeLink, i))
                if id then found[#found + 1] = id end
                -- What it makes and needs, for items the app has no data for yet.
                noteItem(itemIdOf(try(GetTradeSkillItemLink, i)))
                for r = 1, (try(GetTradeSkillNumReagents, i)) or 0 do
                    noteItem(itemIdOf(try(GetTradeSkillReagentItemLink, i, r)))
                end
            end
        end
    end
    if #found == 0 and GetNumCrafts and (try(GetNumCrafts) or 0) > 0 then
        profession = try(GetCraftDisplaySkillLine) or try(GetCraftSkillLine, 1)
        for i = 1, (try(GetNumCrafts)) or 0 do
            local _, _, kind = try(GetCraftInfo, i)
            if kind ~= "header" then
                local id = spellIdOfLink(try(GetCraftRecipeLink, i)) or spellIdOfLink(try(GetCraftItemLink, i))
                if id then found[#found + 1] = id end
            end
        end
    end
    if #found == 0 and C_TradeSkillUI and C_TradeSkillUI.GetAllRecipeIDs then
        for _, id in ipairs((try(C_TradeSkillUI.GetAllRecipeIDs)) or {}) do
            local info = try(C_TradeSkillUI.GetRecipeInfo, id)
            if info and info.learned then found[#found + 1] = id end
        end
        local line = C_TradeSkillUI.GetBaseProfessionInfo and try(C_TradeSkillUI.GetBaseProfessionInfo)
        profession = profession or (line and line.professionName)
    end
    return profession, found
end

local function containerSlots(bag)
    if C_Container and C_Container.GetContainerNumSlots then return (try(C_Container.GetContainerNumSlots, bag)) or 0 end
    return (try(GetContainerNumSlots, bag)) or 0
end

local function containerItem(bag, slot)
    if C_Container and C_Container.GetContainerItemInfo then
        local info = try(C_Container.GetContainerItemInfo, bag, slot)
        if info then return info.itemID, info.stackCount end
        return
    end
    local _, count, _, _, _, _, link = try(GetContainerItemInfo, bag, slot)
    return itemIdOf(link), count
end

local function readBags(bags)
    local items = {}
    for _, bag in ipairs(bags) do
        for slot = 1, containerSlots(bag) do
            local id, count = containerItem(bag, slot)
            if id then
                items[id] = (items[id] or 0) + (count or 1)
                noteItem(id)
            end
        end
    end
    return items
end

local BAG_IDS = { 0, 1, 2, 3, 4 }
local BANK_IDS = { -1, 5, 6, 7, 8, 9, 10, 11 }

local function charText()
    local _, classFile = UnitClass("player")
    local _, build = GetBuildInfo()
    local lines = {
        "!WOWORACLE-CHAR 1",
        "name=" .. clean(charKey()),
        "class=" .. clean(classFile),
        "level=" .. (UnitLevel("player") or 0),
        "build=" .. clean(build),
        "time=" .. time(),
    }
    for name, s in pairs(charState.skills) do
        lines[#lines + 1] = ("skill=%s;%d;%d"):format(clean(name), s[1], s[2])
    end
    for profession, ids in pairs(charState.recipes) do
        lines[#lines + 1] = ("recipes=%s;%s"):format(clean(profession), table.concat(ids, ","))
    end
    local totals = {}
    for id, n in pairs(charState.bags) do totals[id] = (totals[id] or 0) + n end
    for id, n in pairs(charState.bank or {}) do totals[id] = (totals[id] or 0) + n end
    local parts = {}
    for id, n in pairs(totals) do parts[#parts + 1] = id .. ":" .. n end
    lines[#lines + 1] = "items=" .. table.concat(parts, ",")
    lines[#lines + 1] = "bank=" .. (charState.bank and "seen" or "not seen")
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n")
end

local function saveCharacter()
    local store = charStore()
    local key = charKey()
    local old = store[key]
    -- Keep what this session hasn't seen yet (recipes of other professions, the bank) from the last save.
    if old and type(old) == "table" then
        for p, ids in pairs(old.recipes or {}) do if not charState.recipes[p] then charState.recipes[p] = ids end end
        if not charState.bank and old.bank then charState.bank = old.bank end
    end
    store[key] = { recipes = charState.recipes, bank = charState.bank, text = charText() }
end

-- ------------------------------------------------------------------------------------------------
-- Base stats per class, race and level (what a character has with nothing on, no buffs): the game's own numbers,
-- which nobody publishes reliably. Saved for every character you log in, at every level you reach, in
-- WoWOracleDB.base["CLASS:RACE:LEVEL"]; the app uses them in the calculator instead of its estimate.
-- base = the paper doll's stat minus every bonus (gear, buffs) - the same split the character sheet tooltip shows.
-- ------------------------------------------------------------------------------------------------

local function wearsNothing()
    for slot = 1, 19 do
        if slot ~= 4 and slot ~= 19 and GetInventoryItemID("player", slot) then return false end -- shirt/tabard are fine
    end
    return true
end

local function recordBaseStats()
    local _, _, classId = UnitClass("player")
    local _, _, raceId = UnitRace("player")
    local level = UnitLevel("player")
    if not classId or not raceId or not level or level < 1 then return end
    local base, bonus = {}, false
    for i = 1, 5 do
        local stat, _, posBuff, negBuff = UnitStat("player", i)
        -- 0 means the client has no character data (logging out): nothing to record.
        if not stat or stat <= 0 then return end
        base[i] = stat - (posBuff or 0) - (negBuff or 0)
        if (posBuff or 0) ~= 0 or (negBuff or 0) ~= 0 then bonus = true end
    end
    -- Health and mana only mean "base" with nothing on and no bonuses; the app only uses them then.
    local naked = wearsNothing() and not bonus
    local _, build = GetBuildInfo()
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.base = WoWOracleDB.base or {}
    local key = ("%d:%d:%d"):format(classId, raceId, level)
    local old = WoWOracleDB.base[key]
    if old and old.naked and not naked then return end -- keep the better (naked) snapshot
    WoWOracleDB.base[key] = {
        naked = naked,
        text = table.concat({
            "!WOWORACLE-BASE 1",
            ("base=%d;%d;%d;%d;%d;%d;%d;%d"):format(classId, raceId, level, base[1], base[2], base[3], base[4], base[5]),
            ("vitals=%d;%d;%d"):format(UnitHealthMax("player") or 0, UnitPowerMax("player", 0) or 0, naked and 1 or 0),
            "who=" .. clean(select(2, UnitClass("player"))) .. ";" .. clean(select(2, UnitRace("player"))),
            "build=" .. clean(build),
            "time=" .. time(),
            "!END",
        }, "\n"),
    }
end

-- ------------------------------------------------------------------------------------------------
-- Trainer windows: the skill each profession recipe is taught at. The client data doesn't hold it, so the app
-- otherwise guesses. Saved per profession in WoWOracleDB.trainer[profession] (all trainers merged), recipe by name.
-- ------------------------------------------------------------------------------------------------

local trainerFilters = { "available", "unavailable", "used" }

local function trainerText(profession, recipes)
    local _, build = GetBuildInfo()
    local lines = { "!WOWORACLE-TRAINER 1", "profession=" .. clean(profession) }
    for name, value in pairs(recipes) do lines[#lines + 1] = "recipe=" .. value .. ";" .. name end
    lines[#lines + 1] = "build=" .. clean(build)
    lines[#lines + 1] = "time=" .. time()
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n")
end

local function readTrainer()
    if not GetNumTrainerServices then return 0 end
    -- Every service, also the ones the trainer window's filter hides (put back afterwards).
    local switched = {}
    if GetTrainerServiceTypeFilter and SetTrainerServiceTypeFilter then
        for _, kind in ipairs(trainerFilters) do
            if not try(GetTrainerServiceTypeFilter, kind) then
                switched[#switched + 1] = kind
                try(SetTrainerServiceTypeFilter, kind, 1)
            end
        end
    end
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.trainer = WoWOracleDB.trainer or {}
    local touched, count = {}, 0
    for i = 1, (try(GetNumTrainerServices)) or 0 do
        local name, _, category = try(GetTrainerServiceInfo, i)
        if name and category ~= "header" then
            local profession, rank = try(GetTrainerServiceSkillReq, i)
            rank = tonumber(rank) or 0
            if type(profession) == "string" and profession ~= "" and rank > 0 then
                local store = WoWOracleDB.trainer[profession] or { recipes = {} }
                WoWOracleDB.trainer[profession] = store
                store.recipes[clean(name)] = ("%d;%d;%d"):format(rank,
                    itemIdOf(try(GetTrainerServiceItemLink, i)) or 0, tonumber((try(GetTrainerServiceLevelReq, i))) or 0)
                touched[profession] = true
                count = count + 1
            end
        end
    end
    for _, kind in ipairs(switched) do try(SetTrainerServiceTypeFilter, kind, 0) end
    for profession in pairs(touched) do
        local store = WoWOracleDB.trainer[profession]
        store.text = trainerText(profession, store.recipes)
    end
    return count
end

-- Taking gear off fires one event per slot: record once, a moment after the last one.
local baseStatsPending = false
local function baseStatsSoon()
    if baseStatsPending then return end
    baseStatsPending = true
    C_Timer.After(2, function()
        baseStatsPending = false
        pcall(recordBaseStats)
    end)
end

-- While logging out the client has already let go of the character: no health, no stats, no skill lines, half the
-- bags. Reading then would overwrite good data with empty data, so the logout save keeps what was read in play.
local function characterReady()
    return (UnitHealthMax("player") or 0) > 0 and ((UnitStat("player", 1)) or 0) > 0
end

local function refreshCharacter(what)
    if not characterReady() then return end
    if what == "all" then pcall(recordBaseStats) end
    if what == "skills" or what == "all" then
        local skills = readSkills()
        if next(skills) or not next(charState.skills or {}) then charState.skills = skills end
    end
    if what == "bags" or what == "all" then charState.bags = readBags(BAG_IDS) end
    if what == "bank" then charState.bank = readBags(BANK_IDS) end
    if what == "recipes" or what == "all" then
        local profession, ids = readOpenRecipes()
        if profession and profession ~= "" and #ids > 0 then charState.recipes[profession] = ids end
    end
    saveCharacter()
end

-- Bags change a lot (looting, crafting): save at most every few seconds.
local bagsPending = false
local function bagsChanged()
    if bagsPending then return end
    bagsPending = true
    C_Timer.After(3, function()
        bagsPending = false
        pcall(refreshCharacter, "bags")
    end)
end

-- ------------------------------------------------------------------------------------------------
-- Your own gear for the app: the same export as the Oracle button, saved by itself (login, gear or talent
-- changes, level up, logout). The app keeps a gear set and talent set named exactly after the character and
-- updates them; sets under other names ("… Alt SP") are never touched.
-- ------------------------------------------------------------------------------------------------

local SELF_MARKER = "!WOWORACLE-SELF 1"

local function storeSelfExport(text)
    if type(text) ~= "string" or text == "" then return end
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.self = WoWOracleDB.self or {}
    WoWOracleDB.self[charKey()] = SELF_MARKER .. "\n" .. text
end

-- Waits for item data (like the button does), then saves.
local function saveSelfExport()
    whenItemsLoaded("player", function()
        local ok, text = pcall(buildExport, "player", false)
        if ok then storeSelfExport(text) end
    end)
end

-- /reload or /oracle sync: save what the client has right now (at logout it has nothing: keep the last save).
local function saveSelfExportNow()
    if not characterReady() then return end
    local ok, text = pcall(buildExport, "player", false)
    if ok then storeSelfExport(text) end
end

local selfPending = false
local function selfChanged()
    if selfPending then return end
    selfPending = true
    C_Timer.After(3, function()
        selfPending = false
        pcall(saveSelfExport)
    end)
end

-- ------------------------------------------------------------------------------------------------
-- New world data: NPCs, vendors and loot that the app's data (Era + other versions, see Known.lua) doesn't
-- have yet. Only the new bits are saved, once, in WoWOracleDB.seen as text blocks the app reads on start:
--   NPCs you target or mouse over that the app doesn't know (name, level, where you saw it).
--   Vendors the app doesn't know, or known ones selling an item the app has no source for.
--   Loot from mobs/objects the app doesn't know (kills and drops), or items without a known source.
-- Nothing is saved before the app has written Known.lua. "/oracle seen clear" empties the list.
-- ------------------------------------------------------------------------------------------------

local function seenStore(kind)
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.seen = WoWOracleDB.seen or {}
    WoWOracleDB.seen[kind] = WoWOracleDB.seen[kind] or {}
    return WoWOracleDB.seen[kind]
end

-- "npc"/"object" and the id from a unit or object GUID.
local function guidInfo(guid)
    if type(guid) ~= "string" then return nil end
    local kind, _, _, _, _, id = strsplit("-", guid)
    id = tonumber(id)
    if not id then return nil end
    if kind == "Creature" or kind == "Vehicle" then return "npc", id end
    if kind == "GameObject" then return "object", id end
end

local function buildName()
    local _, build = GetBuildInfo()
    return clean(build)
end

-- NPCs ---------------------------------------------------------------------------------------------

local function npcText(n)
    return table.concat({
        "!WOWORACLE-NPC 1",
        "id=" .. n.id,
        "name=" .. clean(n.name),
        "level=" .. (n.level or 0),
        "rank=" .. clean(n.rank),
        "react=" .. clean(n.react),
        ("at=%s;%s;%s"):format(clean(n.zone), tostring(n.x or 0), tostring(n.y or 0)),
        "build=" .. buildName(),
        "time=" .. (n.time or time()),
        "!END",
    }, "\n")
end

local function sawUnit(unit)
    if not UnitExists(unit) or UnitIsPlayer(unit) or UnitPlayerControlled(unit) then return end
    local kind, id = guidInfo(UnitGUID(unit))
    if kind ~= "npc" or isKnown("placed", id) ~= false then return end -- the app knows where it stands
    local store = seenStore("npcs")
    local n = store[id]
    if n and (n.x or 0) ~= 0 then return end -- seen it already (with a position)
    local zone, x, y = playerPlace()
    local react = UnitReaction and UnitReaction(unit, "player")
    n = {
        id = id,
        name = UnitName(unit) or (n and n.name) or "",
        level = UnitLevel(unit) or 0, -- -1 = boss (??)
        rank = UnitClassification(unit) or "",
        react = react and (react >= 5 and "friendly" or react == 4 and "neutral" or "hostile") or "",
        zone = zone, x = x, y = y, time = time(),
    }
    n.text = npcText(n)
    store[id] = n
end

local function seenName(id)
    local n = WoWOracleDB and WoWOracleDB.seen and WoWOracleDB.seen.npcs and WoWOracleDB.seen.npcs[id]
    return n and n.name
end

-- Vendors ------------------------------------------------------------------------------------------

-- item id, price (copper), stack, how many are left (-1 = unlimited) for merchant slot i.
local function merchantItem(i)
    local id = itemIdOf(try(GetMerchantItemLink, i))
    if not id then return nil end
    local price, stack, available, extended
    if C_MerchantFrame and C_MerchantFrame.GetItemInfo then
        local info = try(C_MerchantFrame.GetItemInfo, i)
        if type(info) == "table" then
            price, stack, available, extended = info.price, info.stackCount, info.numAvailable, info.hasExtendedCost
        end
    end
    if price == nil then
        local _, _, p, q, n, _, _, ext = try(GetMerchantItemInfo, i)
        price, stack, available, extended = p, q, n, ext
    end
    return id, price or 0, stack or 1, available or -1, extended and 1 or 0
end

local function vendorText(v)
    local lines = {
        "!WOWORACLE-VENDOR 1",
        "id=" .. v.id,
        "name=" .. clean(v.name),
        ("at=%s;%s;%s"):format(clean(v.zone), tostring(v.x or 0), tostring(v.y or 0)),
        "build=" .. buildName(),
        "time=" .. (v.time or time()),
    }
    for _, item in ipairs(v.items) do lines[#lines + 1] = "item=" .. item end
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n")
end

local function captureVendor()
    local kind, id = guidInfo(UnitGUID("npc"))
    if kind ~= "npc" then return end
    local vendorKnown = isKnown("vendors", id)
    if vendorKnown == nil then return end
    local items, worth = {}, vendorKnown == false
    for i = 1, (try(GetMerchantNumItems)) or 0 do
        local itemId, price, stack, available, extended = merchantItem(i)
        if itemId then
            noteItem(itemId)
            items[#items + 1] = ("%d;%d;%d;%d;%d"):format(itemId, price, stack, available, extended)
            if isKnown("sourced", itemId) == false then worth = true end
        end
    end
    if not worth or #items == 0 then return end
    local zone, x, y = playerPlace()
    local v = { id = id, name = UnitName("npc") or "", zone = zone, x = x, y = y, items = items, time = time() }
    v.text = vendorText(v)
    seenStore("vendors")[id] = v
end

-- Item links can still be loading when the window opens: read once now and once a moment later.
local function onMerchantShow()
    pcall(captureVendor)
    C_Timer.After(1.5, function() if MerchantFrame and MerchantFrame:IsShown() then pcall(captureVendor) end end)
end

-- Loot ---------------------------------------------------------------------------------------------

local countedLoot = {} -- corpse/object GUIDs already counted this session (loot windows can open twice)
local lootReadySeen = false -- this client sends LOOT_READY (then LOOT_OPENED is the same window again)

local function lootText(l)
    local lines = {
        "!WOWORACLE-LOOT 1",
        ("source=%s;%d;%s"):format(l.kind, l.id, clean(l.name)),
        ("at=%s;%s;%s"):format(clean(l.zone), tostring(l.x or 0), tostring(l.y or 0)),
        "kills=" .. l.kills,
        "all=" .. (l.all and 1 or 0), -- 1: every drop is listed; 0: only items the app had no source for
        "build=" .. buildName(),
        "time=" .. (l.time or time()),
    }
    local ids = {}
    for itemId in pairs(l.items) do ids[#ids + 1] = itemId end
    table.sort(ids)
    for _, itemId in ipairs(ids) do
        local d = l.items[itemId]
        lines[#lines + 1] = ("item=%d;%d;%d"):format(itemId, d.drops, d.count) -- dropped in N loots, M in all
    end
    lines[#lines + 1] = "!END"
    return table.concat(lines, "\n")
end

-- Who each loot slot came from: { [guid] = quantity }. Falls back to your dead target.
local function slotSources(slot)
    local out = {}
    if GetLootSourceInfo then
        local r = pack(try(GetLootSourceInfo, slot))
        for i = 1, r.n - 1, 2 do
            if r[i] then out[r[i]] = tonumber(r[i + 1]) or 1 end
        end
    end
    if next(out) == nil and UnitExists("target") and UnitIsDead("target") then out[UnitGUID("target")] = 0 end
    return out
end

local function sourceName(guid, kind, id)
    if UnitGUID("target") == guid then return UnitName("target") end
    if UnitGUID("mouseover") == guid then return UnitName("mouseover") end
    if kind == "npc" then return seenName(id) end
    return nil
end

local function onLootOpened()
    if not hasKnownList() then return end
    local bySource = {} -- guid -> { [itemId] = count }
    for slot = 1, (try(GetNumLootItems)) or 0 do
        local itemId = itemIdOf(try(GetLootSlotLink, slot))
        noteItem(itemId)
        local _, _, quantity = try(GetLootSlotInfo, slot)
        for guid, n in pairs(slotSources(slot)) do
            bySource[guid] = bySource[guid] or {}
            if itemId then
                local count = (n and n > 0) and n or (quantity or 1)
                bySource[guid][itemId] = (bySource[guid][itemId] or 0) + count
            end
        end
    end
    local store = seenStore("loot")
    for guid, items in pairs(bySource) do
        local kind, id = guidInfo(guid)
        if kind and not countedLoot[guid] then
            countedLoot[guid] = true
            local key = kind .. ":" .. id
            local entry = store[key]
            local known = isKnown(kind == "npc" and "npcs" or "objects", id)
            -- Unknown sources: every drop. Known ones: items without a source (and, once tracked, those again,
            -- even after the app has learned them, so the drop rate stays right).
            local all = known == false or (entry ~= nil and entry.all)
            local wanted = {}
            for itemId, count in pairs(items) do
                if all or (entry and entry.items[itemId]) or isKnown("sourced", itemId) == false then wanted[itemId] = count end
            end
            if all or entry or next(wanted) then
                if not entry then
                    local zone, x, y = playerPlace()
                    entry = { kind = kind, id = id, name = "", zone = zone, x = x, y = y, kills = 0, items = {},
                              all = all }
                    store[key] = entry
                end
                entry.name = sourceName(guid, kind, id) or entry.name
                entry.kills = entry.kills + 1
                for itemId, count in pairs(wanted) do
                    local d = entry.items[itemId] or { drops = 0, count = 0 }
                    d.drops, d.count = d.drops + 1, d.count + count
                    entry.items[itemId] = d
                end
                entry.time = time()
                entry.text = lootText(entry)
            end
        end
    end
end

-- Keeps the saved file small (the game reads it at login and writes it at logout, all at once): what the app has
-- taken in since (it's in Known.lua now) is dropped. Loot sources stay, because they keep counting kills.
local function pruneSeen()
    if not hasKnownList() or not (WoWOracleDB and WoWOracleDB.seen) then return end
    local function prune(store, kind)
        for id in pairs(store or {}) do
            if isKnown(kind, id) then store[id] = nil end
        end
    end
    prune(WoWOracleDB.seen.npcs, "placed")
    prune(WoWOracleDB.seen.vendors, "vendors")
    prune(WoWOracleDB.seen.items, "items")
    -- Quests only once the downloaded data has them (the ones the addon reported stay listed as new on purpose,
    -- so their rewards still fill in; they're few).
    for id in pairs(WoWOracleDB.quests or {}) do
        if isKnown("quests", id) then WoWOracleDB.quests[id] = nil end
    end
end

local function seenCounts()
    local counts = {}
    for _, kind in ipairs({ "npcs", "vendors", "loot", "items" }) do
        local n = 0
        for _ in pairs(seenStore(kind)) do n = n + 1 end
        counts[kind] = n
    end
    return counts
end

-- ------------------------------------------------------------------------------------------------
-- Where subzones are: while you're in one (Bandarion Keep, Freewind Post…), your spot on the zone map is noted every
-- few seconds and averaged, so the app's map search can place subzones its data has no spot for. One small entry
-- per subzone in WoWOracleDB.places; it stops counting after 60 samples.
-- ------------------------------------------------------------------------------------------------

local PLACE_SAMPLES = 60

local function notePlace()
    if not characterReady() then return end
    local sub = (GetSubZoneText and try(GetSubZoneText)) or ""
    local zoneName, x, y = playerPlace()
    if sub == "" or sub == zoneName or zoneName == "" or (x or 0) <= 0 or (y or 0) <= 0 then return end
    WoWOracleDB = WoWOracleDB or {}
    WoWOracleDB.places = WoWOracleDB.places or {}
    local key = zoneName .. ":" .. sub
    local p = WoWOracleDB.places[key] or { n = 0, x = 0, y = 0 }
    if p.n >= PLACE_SAMPLES then return end
    p.n, p.x, p.y = p.n + 1, p.x + x, p.y + y
    p.text = ("!WOWORACLE-PLACE 1\nplace=%s;%s;%.1f;%.1f;%d\n!END"):format(clean(zoneName), clean(sub), p.x / p.n, p.y / p.n, p.n)
    WoWOracleDB.places[key] = p
end

-- ------------------------------------------------------------------------------------------------
-- Startup
-- ------------------------------------------------------------------------------------------------

local events = CreateFrame("Frame")
events:RegisterEvent("ADDON_LOADED")
events:RegisterEvent("PLAYER_LOGIN")
events:RegisterEvent("PLAYER_LOGOUT")
events:RegisterEvent("QUEST_DETAIL")
events:RegisterEvent("QUEST_ACCEPTED")
events:RegisterEvent("QUEST_COMPLETE")
for _, e in ipairs({ "SKILL_LINES_CHANGED", "TRADE_SKILL_SHOW", "TRADE_SKILL_UPDATE", "TRADE_SKILL_LIST_UPDATE", "CRAFT_SHOW",
                     "CRAFT_UPDATE", "BAG_UPDATE_DELAYED", "BANKFRAME_OPENED", "PLAYERBANKSLOTS_CHANGED",
                     "PLAYER_EQUIPMENT_CHANGED", "TRAIT_CONFIG_UPDATED", "PLAYER_TALENT_UPDATE", "CHARACTER_POINTS_CHANGED",
                     "PLAYER_LEVEL_UP", "PLAYER_TARGET_CHANGED", "UPDATE_MOUSEOVER_UNIT", "MERCHANT_SHOW", "LOOT_READY",
                     "LOOT_OPENED", "TRAINER_SHOW", "ZONE_CHANGED", "ZONE_CHANGED_INDOORS", "ZONE_CHANGED_NEW_AREA" }) do
    pcall(events.RegisterEvent, events, e) -- some don't exist on every client
end
events:SetScript("OnEvent", function(_, event, name, extra)
    if event == "ADDON_LOADED" then
        if name == ADDON then
            WoWOracleDB = WoWOracleDB or {}
        elseif name == "Blizzard_InspectUI" then
            addInspectButton()
        end
    elseif event == "PLAYER_LOGIN" then
        addCharacterButton()
        if InspectFrame then addInspectButton() end -- already loaded by another addon
        -- Talent data can arrive a little after login.
        C_Timer.After(8, function() pcall(captureTalentLayout, true) end)
        C_Timer.After(10, function() pcall(scanQuestLog) end)
        C_Timer.After(20, function() pcall(pruneSeen) end)
        C_Timer.After(6, function() pcall(refreshCharacter, "all") end)
        C_Timer.After(7, function() pcall(saveSelfExport) end)
        if C_Timer.NewTicker then C_Timer.NewTicker(8, function() pcall(notePlace) end) end
    elseif event == "PLAYER_LOGOUT" then
        pcall(captureTalentLayout, true) -- latest tree into the saved file
        pcall(refreshCharacter, "all")
        pcall(saveSelfExportNow)
    elseif event == "PLAYER_EQUIPMENT_CHANGED" or event == "TRAIT_CONFIG_UPDATED" or event == "PLAYER_TALENT_UPDATE"
        or event == "CHARACTER_POINTS_CHANGED" or event == "PLAYER_LEVEL_UP" then
        selfChanged()
        -- New level: its stats arrive a moment later. Gear off: the naked snapshot (health and mana too).
        if event == "PLAYER_LEVEL_UP" or event == "PLAYER_EQUIPMENT_CHANGED" then baseStatsSoon() end
    elseif event == "SKILL_LINES_CHANGED" then
        pcall(refreshCharacter, "skills")
    elseif event == "TRADE_SKILL_SHOW" or event == "TRADE_SKILL_UPDATE" or event == "TRADE_SKILL_LIST_UPDATE"
        or event == "CRAFT_SHOW" or event == "CRAFT_UPDATE" then
        pcall(refreshCharacter, "recipes")
    elseif event == "BAG_UPDATE_DELAYED" then
        bagsChanged()
    elseif event == "BANKFRAME_OPENED" or event == "PLAYERBANKSLOTS_CHANGED" then
        pcall(refreshCharacter, "bank")
    elseif event == "QUEST_DETAIL" then
        pcall(onQuestDetail)
    elseif event == "QUEST_ACCEPTED" then
        pcall(onQuestAccepted, name, extra)
    elseif event == "QUEST_COMPLETE" then
        pcall(onQuestComplete)
    elseif event == "PLAYER_TARGET_CHANGED" then
        pcall(sawUnit, "target")
    elseif event == "UPDATE_MOUSEOVER_UNIT" then
        pcall(sawUnit, "mouseover")
    elseif event == "MERCHANT_SHOW" then
        onMerchantShow()
    elseif event == "ZONE_CHANGED" or event == "ZONE_CHANGED_INDOORS" or event == "ZONE_CHANGED_NEW_AREA" then
        C_Timer.After(1, function() pcall(notePlace) end) -- the map position settles a moment after
    elseif event == "TRAINER_SHOW" then
        C_Timer.After(0.3, function() pcall(readTrainer) end) -- the list fills right after the window opens
    elseif event == "LOOT_READY" or event == "LOOT_OPENED" then
        -- Clients with LOOT_READY send both for one loot window: read it once.
        if event == "LOOT_READY" then lootReadySeen = true end
        if event == "LOOT_READY" or not lootReadySeen then pcall(onLootOpened) end
    end
end)

SLASH_WOWORACLE1 = "/oracle"
SlashCmdList.WOWORACLE = function(msg)
    msg = (msg or ""):lower():match("^%s*(.-)%s*$")
    if msg == "debug" then
        debug()
    elseif msg == "t" or msg == "target" or msg == "inspect" then
        WoWOracle_CopyInspect("target")
    elseif msg == "" or msg == "me" then
        WoWOracle_CopySelf()
    elseif msg == "quests" then
        say(("%d quests saved; the app reads them on start (after /reload or logout)."):format(questCount()))
    elseif msg == "quests clear" then
        WoWOracleDB.quests = {}
        say("saved quests cleared (the app keeps the ones it already read).")
    elseif msg == "seen" then
        local c = seenCounts()
        say(("new to the app: %d NPCs, %d vendors, %d loot sources, %d items (read on start, after /reload or logout)."):format(c.npcs, c.vendors, c.loot, c.items))
        say(knownSummary())
    elseif msg == "base" then
        pcall(recordBaseStats)
        local n = 0
        for _ in pairs(WoWOracleDB.base or {}) do n = n + 1 end
        local parts = {}
        for i = 1, 5 do
            local stat, _, posBuff, negBuff = UnitStat("player", i)
            parts[#parts + 1] = tostring((stat or 0) - (posBuff or 0) - (negBuff or 0))
        end
        say(("base stats now (str agi sta int spi): %s. Saved for %d class/race/level combinations%s."):format(
            table.concat(parts, " "), n, wearsNothing() and " (naked: health and mana too)" or ""))
    elseif msg == "trainer" then
        local ok, n = pcall(readTrainer)
        local known = 0
        for _, store in pairs(WoWOracleDB.trainer or {}) do
            for _ in pairs(store.recipes or {}) do known = known + 1 end
        end
        say(ok and n > 0 and ("read %d recipes from this trainer; %d saved in all."):format(n, known)
            or ("open a profession trainer first; %d recipes saved so far."):format(known))
    elseif msg == "seen clear" then
        WoWOracleDB.seen = {}
        say("saved NPCs, vendors and loot cleared (the app keeps what it already read).")
    elseif msg == "sync" then
        pcall(refreshCharacter, "all")
        pcall(saveSelfExportNow)
        pcall(captureTalentLayout, true)
        say("saved; reloading so the game writes the file for the app…")
        C_Timer.After(0.5, ReloadUI)
    elseif msg == "talents" then
        if captureTalentLayout(false) == 0 then say("couldn't read the talent tree.") end
    else
        say("/oracle (you), /oracle t (inspect your target), /oracle sync (save now for the app), /oracle talents, /oracle quests [clear], /oracle seen [clear], /oracle base, /oracle trainer, /oracle debug")
    end
end

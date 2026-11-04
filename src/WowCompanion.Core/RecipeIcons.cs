namespace WowCompanion.Core;

/// <summary>
/// An icon for recipes that make no item (enchants), which the game data leaves without a picture: the enchanting
/// material of that skill bracket, so the list shows how far up the profession a recipe sits:
///   1-49 Strange Dust · 50-99 Lesser Magic Essence · 100-149 Soul Dust · 150-199 Vision Dust ·
///   200-249 Dream Dust · 250-289 Illusion Dust · 290+ Large Brilliant Shard.
/// Other recipes without an item get the same by skill. Values are the icons' FileDataIDs (wowdev listfile).
/// </summary>
public static class RecipeIcons
{
    private static readonly (int FromSkill, int Icon)[] Brackets =
    [
        (290, 132873), // inv_enchant_shardbrilliantlarge
        (250, 132856), // inv_enchant_dustillusion
        (200, 132855), // inv_enchant_dustdream
        (150, 132859), // inv_enchant_dustvision
        (100, 132857), // inv_enchant_dustsoul
        (50, 132867),  // inv_enchant_essencemagicsmall
        (0, 132858),   // inv_enchant_duststrange
    ];

    /// <summary>The recipe's own icon, or the bracket icon when it has none.</summary>
    public static int Or(int iconId, int minSkill) =>
        iconId > 0 ? iconId : Brackets.First(b => minSkill >= b.FromSkill).Icon;
}

namespace WowCompanion.Data;

/// <summary>One spell effect as the client stores it. Values are at the spell's own level.</summary>
public sealed record EffectFacts(
    int Index, int Effect, int Aura, double BasePoints, double PerLevel, double Variance,
    double SpellCoefficient, double ApCoefficient, int PeriodMs, int TriggerSpell,
    int Misc0, int Misc1, int ChainTargets, int RadiusIndex, double Amplitude, double PointsPerCombo,
    int Mask0, int Mask1, int Mask2, int Mask3, int Target0);

/// <summary>Everything the builder knows about one spell before it is written out.</summary>
public sealed class SpellFacts
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public string Rank { get; set; } = "";
    public string Description { get; set; } = "";
    public string AuraDescription { get; set; } = "";
    public int SchoolMask { get; set; }
    public int CastMs { get; set; }
    public int DurationMs { get; set; }
    public int RangeIndex { get; set; }
    public double RangeMax { get; set; }
    public string RangeName { get; set; } = "";
    public int CooldownMs { get; set; }
    public int CategoryCooldownMs { get; set; }
    public int GcdMs { get; set; }
    public int PowerType { get; set; }
    public int PowerCost { get; set; }
    public int PowerCostPct { get; set; }
    public int PowerPerSecond { get; set; }
    public int BaseLevel { get; set; }
    public int SpellLevel { get; set; }
    public int MaxLevel { get; set; }
    public int ProcChance { get; set; }
    public int ProcCharges { get; set; }
    public int MaxStacks { get; set; }
    public int ClassSet { get; set; }
    /// <summary>SpellClassOptions masks: which spell "family" this is, matched by talent modifiers.</summary>
    public string ClassMask { get; set; } = "0,0,0,0";
    public int IconFileId { get; set; }
    public List<EffectFacts> Effects { get; private set; } = [];

    /// <summary>A copy with every effect value multiplied by rank (multi-rank talents use one spell for all ranks).</summary>
    public SpellFacts Scaled(int rank)
    {
        var copy = (SpellFacts)MemberwiseClone();
        copy.Effects = Effects.Select(e => e with { BasePoints = e.BasePoints * rank }).ToList();
        return copy;
    }

    /// <summary>
    /// The talent at a rank: trait talents with per-rank values (TraitDefinitionEffectPoints curves) use those for their
    /// effects; any other effect is multiplied by the rank.
    /// </summary>
    public SpellFacts AtRank(int rank, IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>>? points)
    {
        var copy = Scaled(rank);
        if (points is null || points.Count == 0) return copy;
        copy.Effects = Effects.Select(e =>
                points.TryGetValue(e.Index, out var curve) && curve.TryGetValue(rank, out var value)
                    ? e with { BasePoints = value }
                    : e with { BasePoints = e.BasePoints * rank })
            .ToList();
        return copy;
    }

    public EffectFacts? Effect(int index) => Effects.FirstOrDefault(e => e.Index == index);
}

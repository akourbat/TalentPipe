namespace SpellTalentSystem;

// ============================================================================
// STATUS EFFECTS: BOONS, CONDITIONS, CONTROL (CC), AND AURAS (Full GW2 Set)
// ============================================================================

public enum BoonType
{
    Might,
    Fury,
    Quickness,
    Alacrity,
    Protection,
    Regeneration,
    Swiftness,
    Aegis,
    Stability,
    Resolution,
    Resistance,
    Vigor
}

public enum DamagingConditionType
{
    Bleeding,
    Burning,
    Poisoned,
    Torment,
    Confusion
}

public enum SoftConditionType
{
    Vulnerability,
    Weakness,
    Crippled,
    Chilled,
    Blinded,
    Immobile,
    Slow
}

public enum AuraType
{
    Fire,
    Frost,
    Shocking,
    Magnetic,
    Light,
    Chaos,
    Dark
}

public union ControlKind(
    ControlKind.Stun,
    ControlKind.Daze,
    ControlKind.Knockdown,
    ControlKind.Knockback,
    ControlKind.Pull,
    ControlKind.Launch,
    ControlKind.Sink,
    ControlKind.Float,
    ControlKind.Fear,
    ControlKind.Taunt)
{
    public readonly record struct Stun;
    public readonly record struct Daze;
    public readonly record struct Knockdown;
    public readonly record struct Knockback(StatValue<double> DistanceMeters);
    public readonly record struct Pull(StatValue<double> DistanceMeters);
    public readonly record struct Launch(StatValue<double> DistanceMeters);
    public readonly record struct Sink;
    public readonly record struct Float;
    public readonly record struct Fear;
    public readonly record struct Taunt;
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

public union StatusEffect(
    StatusEffect.Boon,
    StatusEffect.DamagingCondition,
    StatusEffect.SoftCondition,
    StatusEffect.Control,
    StatusEffect.Aura)
{
    public readonly record struct Boon(
        BoonType Type,
        StatValue<double> DurationSeconds,
        StatValue<int> Stacks,
        StackingMode? StackingOverride = null)
    {
        public StackingMode Stacking => StackingOverride ?? Type.GetDefaultStacking();
    }

    public readonly record struct DamagingCondition(
        DamagingConditionType Type,
        StatValue<double> TickEffect,
        StatValue<double> DurationSeconds,
        StatValue<double> TickIntervalSeconds,
        StatValue<int> Stacks);

    public readonly record struct SoftCondition(
        SoftConditionType Type,
        StatValue<double> DurationSeconds,
        StatValue<int> Stacks,
        StackingMode? StackingOverride = null)
    {
        public StackingMode Stacking => StackingOverride ?? Type.GetDefaultStacking();
    }

    public readonly record struct Control(
        ControlKind Kind,
        StatValue<double> DurationSeconds,
        StatValue<double> BreakbarDamage);

    public readonly record struct Aura(
        AuraType Type,
        StatValue<double> DurationSeconds);
    public override string ToString() => Value?.ToString() ?? string.Empty;
}
// ============================================================================
// STACKING RULES (Guild Wars 2 Intensity vs. Duration Stacking)
// ============================================================================

public enum StackingMode
{
    /// <summary>Multiple applications increase potency simultaneously (e.g., Might, Stability, Bleeding, Vulnerability).</summary>
    Intensity,

    /// <summary>Multiple applications queue or extend total active time (e.g., Fury, Quickness, Regeneration, Chilled).</summary>
    Duration
}

public static class StatusStackingRules
{
    public static StackingMode GetDefaultStacking(this BoonType type) => type switch
    {
        BoonType.Might or BoonType.Stability => StackingMode.Intensity,
        _ => StackingMode.Duration
    };

    public static StackingMode GetDefaultStacking(this SoftConditionType type) => type switch
    {
        SoftConditionType.Vulnerability => StackingMode.Intensity,
        _ => StackingMode.Duration
    };
}


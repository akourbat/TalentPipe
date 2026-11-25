namespace SpellTalentSystem;

// ============================================================================
// 1. NATURES & DAMAGE SCHOOLS
// ============================================================================

public enum ElementType { Fire, Frost, Lightning, Earth, Arcane, Shadow, Light }
public enum PhysDamageType { Slashing, Piercing, Blunt }

public union AbilityNature(
    AbilityNature.Element,
    AbilityNature.Physical,
    AbilityNature.Healing,
    AbilityNature.Utility)
{
    public readonly record struct Element(ElementType Type);
    public readonly record struct Physical(PhysDamageType Type);
    public readonly record struct Healing;
    public readonly record struct Utility;
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

// ============================================================================
// 2. TARGETING, DELIVERY & COMBO MECHANICS (Full GW2 Set)
// ============================================================================

public union Targeting(
    Targeting.Self,
    Targeting.SingleTarget,
    Targeting.PointBlankAoE,
    Targeting.GroundAoE)
{
    public readonly record struct Self;
    public readonly record struct SingleTarget(StatValue<double> Range);
    public readonly record struct PointBlankAoE(StatValue<double> Radius, StatValue<int> MaxTargets);
    public readonly record struct GroundAoE(StatValue<double> Range, StatValue<double> Radius, StatValue<int> MaxTargets);
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

public enum ComboFieldType
{
    Fire,
    Ice,
    Lightning,
    Water,
    Smoke,
    Dark,
    Light,
    Ethereal,
    Poison
}

public union ComboFinisher(
    ComboFinisher.Blast,
    ComboFinisher.Projectile,
    ComboFinisher.Leap,
    ComboFinisher.Whirl)
{
    public readonly record struct Blast;
    public readonly record struct Projectile(StatValue<double> TriggerChance);
    public readonly record struct Leap;
    public readonly record struct Whirl;
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

public union DeliveryMode(
    DeliveryMode.Instant,
    DeliveryMode.Melee,
    DeliveryMode.Projectile,
    DeliveryMode.Blast,
    DeliveryMode.Beam,
    DeliveryMode.Pulse)
{
    public readonly record struct Instant;
    public readonly record struct Melee(StatValue<int> CleaveTargets, ComboFinisher? Finisher = null);
    public readonly record struct Projectile(StatValue<double> Speed, StatValue<int> PierceCount, ComboFinisher? Finisher = null);
    public readonly record struct Blast(ComboFinisher? Finisher = null);
    public readonly record struct Beam(StatValue<double> TickIntervalSeconds);
    public readonly record struct Pulse(StatValue<double> PulseIntervalSeconds);
    public override string ToString() => Value?.ToString() ?? string.Empty;
}

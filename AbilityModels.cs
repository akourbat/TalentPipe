using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace SpellTalentSystem;

// ============================================================================
// 1. ABILITY EFFECTS
// ============================================================================

public enum EffectTarget { Self, Enemy, Ally }

public union AbilityEffect(
    AbilityEffect.DirectDamage,
    AbilityEffect.DirectHeal,
    AbilityEffect.HealOverTime,
    AbilityEffect.Barrier,
    AbilityEffect.ApplyStatus,
    AbilityEffect.CleanseConditions,
    AbilityEffect.StripBoons,
    AbilityEffect.CreateComboField)
{
    public readonly record struct DirectDamage(StatValue<double> Effect);
    public readonly record struct DirectHeal(StatValue<double> Effect);
    public readonly record struct HealOverTime(
        StatValue<double> TickEffect,
        StatValue<double> DurationSeconds,
        StatValue<double> TickIntervalSeconds);
    public readonly record struct Barrier(StatValue<double> Effect, StatValue<double> DurationSeconds);
    public readonly record struct ApplyStatus(StatusEffect Status, EffectTarget Target);
    public readonly record struct CleanseConditions(StatValue<int> Count, EffectTarget Target);
    public readonly record struct StripBoons(StatValue<int> Count, EffectTarget Target);
    public readonly record struct CreateComboField(ComboFieldType Field, StatValue<double> DurationSeconds, StatValue<double> Radius);

    public override string ToString() => Value?.ToString() ?? string.Empty;
}

// ============================================================================
// 2. UNIFIED ABILITY MODEL (Supports Head + Dependent SubAbilities)
// ============================================================================

public sealed record Ability(
    string Id,
    string Name,
    AbilityNature Nature,
    Targeting Targeting,
    DeliveryMode Delivery,
    ImmutableArray<AbilityEffect> Effects,
    StatValue<double> CastTimeSeconds,
    StatValue<double> CooldownSeconds,
    ImmutableArray<Ability> SubAbilities = default)
{
    public ImmutableArray<Ability> SubAbilities { get; init; } =
        SubAbilities.IsDefault ? ImmutableArray<Ability>.Empty : SubAbilities;

    public bool Equals(Ability? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return Id == other.Id
            && Name == other.Name
            && EqualityComparer<AbilityNature>.Default.Equals(Nature, other.Nature)
            && EqualityComparer<Targeting>.Default.Equals(Targeting, other.Targeting)
            && EqualityComparer<DeliveryMode>.Default.Equals(Delivery, other.Delivery)
            && CastTimeSeconds == other.CastTimeSeconds
            && CooldownSeconds == other.CooldownSeconds
            && Effects.AsSpan().SequenceEqual(other.Effects.AsSpan())
            && SubAbilities.AsSpan().SequenceEqual(other.SubAbilities.AsSpan());
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Name);
        hash.Add(Nature);
        hash.Add(Targeting);
        hash.Add(Delivery);
        hash.Add(CastTimeSeconds);
        hash.Add(CooldownSeconds);
        foreach (var e in Effects) hash.Add(e);
        foreach (var s in SubAbilities) hash.Add(s);
        return hash.ToHashCode();
    }
}

// ============================================================================
// 3. TALENT & RESOLVED TREE MODELS
// ============================================================================

public sealed record Talent(
    string Id,
    string Name,
    int Priority,
    Func<Ability, bool> AppliesTo,
    Func<Ability, Ability>? Transform = null,
    Func<Ability, IEnumerable<Ability>>? SpawnSubAbilities = null);

public sealed record ResolvedAbilityNode(
    Ability EffectiveAbility,
    ImmutableArray<string> AppliedTalentIds,
    ImmutableArray<ResolvedAbilityNode> SubAbilities)
{
    public bool Equals(ResolvedAbilityNode? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return EqualityComparer<Ability>.Default.Equals(EffectiveAbility, other.EffectiveAbility)
            && AppliedTalentIds.AsSpan().SequenceEqual(other.AppliedTalentIds.AsSpan())
            && SubAbilities.AsSpan().SequenceEqual(other.SubAbilities.AsSpan());
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EffectiveAbility);
        foreach (var id in AppliedTalentIds)
            hash.Add(id);
        foreach (var sub in SubAbilities)
            hash.Add(sub);
        return hash.ToHashCode();
    }
}

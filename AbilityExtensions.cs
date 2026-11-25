using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SpellTalentSystem;

public static class AbilityExtensions
{
    // ========================================================================
    // QUERY PREDICATES (For Concise Talent `AppliesTo` Rules)
    // ========================================================================

    public static bool IsElement(this Ability ability, ElementType? type = null) =>
        ability.Nature is AbilityNature.Element(var t) && (type is null || t == type);

    public static bool IsPhysical(this Ability ability, PhysDamageType? type = null) =>
        ability.Nature is AbilityNature.Physical(var t) && (type is null || t == type);

    public static bool IsHealing(this Ability ability) =>
        ability.Nature is AbilityNature.Healing;

    public static bool HasEffect<TEffect>(this Ability ability) =>
        ability.Effects.Any(e => e.Value is TEffect);

    public static bool HasHealingEffect(this Ability ability) =>
        ability.Effects.Any(e => e.Value is AbilityEffect.DirectHeal or AbilityEffect.HealOverTime or AbilityEffect.Barrier);

    public static bool GrantsBoon(this Ability ability, BoonType? type = null) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.Boon(var t, _, _, _), _) &&
            (type is null || t == type));

    public static bool GrantsBoonWithStacking(this Ability ability, StackingMode stacking) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.Boon boon, _) &&
            boon.Stacking == stacking);

    public static bool AppliesCondition(this Ability ability, DamagingConditionType? type = null) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.DamagingCondition(var t, _, _, _, _), _) &&
            (type is null || t == type));

    public static bool AppliesSoftCondition(this Ability ability, SoftConditionType? type = null) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.SoftCondition(var t, _, _, _), _) &&
            (type is null || t == type));

    public static bool AppliesSoftConditionWithStacking(this Ability ability, StackingMode stacking) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.SoftCondition cond, _) &&
            cond.Stacking == stacking);

    public static bool AppliesControl(this Ability ability) =>
        ability.Effects.Any(e => e is AbilityEffect.ApplyStatus(StatusEffect.Control, _));

    public static bool GrantsAura(this Ability ability, AuraType? type = null) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.ApplyStatus(StatusEffect.Aura(var t, _), _) &&
            (type is null || t == type));

    public static bool CreatesComboField(this Ability ability, ComboFieldType? field = null) =>
        ability.Effects.Any(e =>
            e is AbilityEffect.CreateComboField(var f, _, _) &&
            (field is null || f == field));

    public static bool HasFinisher<TFinisher>(this Ability ability) => ability.Delivery switch
    {
        DeliveryMode.Melee(_, var f) => f is TFinisher,
        DeliveryMode.Projectile(_, _, var f) => f is TFinisher,
        DeliveryMode.Blast(var f) => f is TFinisher,
        _ => false
    };

    public static bool HasRadius(this Ability ability) =>
        ability.Targeting is Targeting.PointBlankAoE or Targeting.GroundAoE;

    public static bool HasRange(this Ability ability) =>
        ability.Targeting is Targeting.SingleTarget or Targeting.GroundAoE;

    // ========================================================================
    // TARGETING HELPERS
    // ========================================================================

    public static Ability MapTargeting(this Ability ability, Func<Targeting, Targeting> mapper) =>
        ability with { Targeting = mapper(ability.Targeting) };

    public static Ability MapRange(this Ability ability, Func<StatValue<double>, StatValue<double>> mapper) =>
        ability.MapTargeting(t => t switch
        {
            Targeting.SingleTarget(var range) => new Targeting.SingleTarget(mapper(range)),
            Targeting.GroundAoE(var range, var radius, var maxTargets) => new Targeting.GroundAoE(mapper(range), radius, maxTargets),
            _ => t
        });

    public static Ability MapRadius(this Ability ability, Func<StatValue<double>, StatValue<double>> mapper) =>
        ability.MapTargeting(t => t switch
        {
            Targeting.PointBlankAoE(var radius, var maxTargets) => new Targeting.PointBlankAoE(mapper(radius), maxTargets),
            Targeting.GroundAoE(var range, var radius, var maxTargets) => new Targeting.GroundAoE(range, mapper(radius), maxTargets),
            _ => t
        });

    public static Ability MapMaxTargets(this Ability ability, Func<StatValue<int>, StatValue<int>> mapper) =>
        ability.MapTargeting(t => t switch
        {
            Targeting.PointBlankAoE(var radius, var maxTargets) => new Targeting.PointBlankAoE(radius, mapper(maxTargets)),
            Targeting.GroundAoE(var range, var radius, var maxTargets) => new Targeting.GroundAoE(range, radius, mapper(maxTargets)),
            _ => t
        });

    // ========================================================================
    // EFFECT, STATUS & SUB-ABILITY MUTATION HELPERS
    // ========================================================================

    public static Ability MapEffects(this Ability ability, Func<AbilityEffect, AbilityEffect> mapper) =>
        ability with { Effects = [.. ability.Effects.Select(mapper)] };

    /// <summary>
    /// Transforms any <see cref="AbilityEffect.ApplyStatus"/> whose payload matches <typeparamref name="TStatus"/>,
    /// preserving the original <see cref="EffectTarget"/> without manual unwrapping.
    /// </summary>
    public static Ability MapStatus<TStatus>(
        this Ability ability,
        Func<TStatus, StatusEffect> mapper,
        EffectTarget? targetFilter = null) =>
        ability.MapEffects(effect =>
            effect is AbilityEffect.ApplyStatus(TStatus status, var target) &&
            (targetFilter is null || target == targetFilter)
                ? new AbilityEffect.ApplyStatus(mapper(status), target)
                : effect);

    /// <summary>
    /// Transforms boon applications on an ability, optionally filtered by <see cref="BoonType"/>,
    /// <see cref="StackingMode"/>, or <see cref="EffectTarget"/>.
    /// </summary>
    public static Ability MapBoons(
        this Ability ability,
        Func<StatusEffect.Boon, StatusEffect.Boon> mapper,
        BoonType? typeFilter = null,
        StackingMode? stackingFilter = null,
        EffectTarget? targetFilter = null) =>
        ability.MapStatus<StatusEffect.Boon>(
            boon => (typeFilter is null || boon.Type == typeFilter) &&
                    (stackingFilter is null || boon.Stacking == stackingFilter)
                ? mapper(boon)
                : boon,
            targetFilter);

    /// <summary>
    /// Transforms damaging condition applications (Burning, Bleeding, Poisoned, Torment, Confusion),
    /// optionally filtered by <see cref="DamagingConditionType"/> or <see cref="EffectTarget"/>.
    /// </summary>
    public static Ability MapConditions(
        this Ability ability,
        Func<StatusEffect.DamagingCondition, StatusEffect.DamagingCondition> mapper,
        DamagingConditionType? typeFilter = null,
        EffectTarget? targetFilter = null) =>
        ability.MapStatus<StatusEffect.DamagingCondition>(
            cond => (typeFilter is null || cond.Type == typeFilter)
                ? mapper(cond)
                : cond,
            targetFilter);

    /// <summary>
    /// Transforms non-damaging soft condition applications (Vulnerability, Chilled, Crippled, etc.),
    /// optionally filtered by <see cref="SoftConditionType"/>, <see cref="StackingMode"/>, or <see cref="EffectTarget"/>.
    /// </summary>
    public static Ability MapSoftConditions(
        this Ability ability,
        Func<StatusEffect.SoftCondition, StatusEffect.SoftCondition> mapper,
        SoftConditionType? typeFilter = null,
        StackingMode? stackingFilter = null,
        EffectTarget? targetFilter = null) =>
        ability.MapStatus<StatusEffect.SoftCondition>(
            cond => (typeFilter is null || cond.Type == typeFilter) &&
                    (stackingFilter is null || cond.Stacking == stackingFilter)
                ? mapper(cond)
                : cond,
            targetFilter);

    public static Ability AddEffect(this Ability ability, AbilityEffect effect) =>
        ability with { Effects = ability.Effects.Add(effect) };

    public static Ability AddSubAbility(this Ability ability, Ability child) =>
        ability with { SubAbilities = ability.SubAbilities.Add(child) };

    // ========================================================================
    // UNION LINQ HELPERS (Unwraps C# 15 IUnion.Value cleanly)
    // ========================================================================

    public static IEnumerable<TCase> OfUnionCase<TUnion, TCase>(this ImmutableArray<TUnion> source)
        where TUnion : struct, IUnion
    {
        foreach (var item in source)
        {
            if (item.Value is TCase matched)
                yield return matched;
        }
    }

    public static IEnumerable<TCase> OfUnionCase<TUnion, TCase>(this IEnumerable<TUnion> source)
        where TUnion : struct, IUnion
    {
        foreach (var item in source)
        {
            if (item.Value is TCase matched)
                yield return matched;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using DynamicData;
using SpellTalentSystem;
using Xunit;

namespace SpellTalentSystem.Tests;

public sealed class SpellTalentPipelineTests
{
    private static Ability CreateFireball() => new(
        Id: "fireball",
        Name: "Fireball",
        Nature: new AbilityNature.Element(ElementType.Fire),
        Targeting: new Targeting.SingleTarget(Range: 900.0),
        Delivery: new DeliveryMode.Projectile(Speed: 1200.0, PierceCount: 0, Finisher: new ComboFinisher.Projectile(0.20)),
        Effects: [new AbilityEffect.DirectDamage(250.0)],
        CastTimeSeconds: 0.75,
        CooldownSeconds: 0.0);

    private static Ability CreateMeteorShower() => new(
        Id: "meteor_shower",
        Name: "Meteor Shower",
        Nature: new AbilityNature.Element(ElementType.Fire),
        Targeting: new Targeting.GroundAoE(Range: 1200.0, Radius: 360.0, MaxTargets: 5),
        Delivery: new DeliveryMode.Pulse(PulseIntervalSeconds: 0.4),
        Effects:
        [
            new AbilityEffect.DirectDamage(310.0)
        ],
        CastTimeSeconds: 3.25,
        CooldownSeconds: 30.0,
        SubAbilities:
        [
            new Ability(
                Id: "meteor_shower_aftershock",
                Name: "Meteor Aftershock",
                Nature: new AbilityNature.Element(ElementType.Fire),
                Targeting: new Targeting.PointBlankAoE(Radius: 120.0, MaxTargets: 5),
                Delivery: new DeliveryMode.Blast(new ComboFinisher.Blast()),
                Effects:
                [
                    new AbilityEffect.ApplyStatus(
                        new StatusEffect.Control(new ControlKind.Knockdown(), DurationSeconds: 1.0, BreakbarDamage: 100.0),
                        EffectTarget.Enemy)
                ],
                CastTimeSeconds: 0.0,
                CooldownSeconds: 0.0)
        ]);

    private static Ability CreateSeverArtery() => new(
        Id: "sever_artery",
        Name: "Sever Artery",
        Nature: new AbilityNature.Physical(PhysDamageType.Slashing),
        Targeting: new Targeting.SingleTarget(Range: 130.0),
        Delivery: new DeliveryMode.Melee(CleaveTargets: 3),
        Effects:
        [
            new AbilityEffect.DirectDamage(120.0),
            new AbilityEffect.ApplyStatus(
                new StatusEffect.DamagingCondition(
                    DamagingConditionType.Bleeding,
                    TickEffect: 35.0,
                    DurationSeconds: 6.0,
                    TickIntervalSeconds: 1.0,
                    Stacks: 2),
                EffectTarget.Enemy)
        ],
        CastTimeSeconds: 0.5,
        CooldownSeconds: 0.0);

    [Fact]
    public void BoonStackingMode_Distinguishes_IntensityVsDuration_InTalentRules()
    {
        var heroicShout = new Ability(
            Id: "heroic_shout",
            Name: "For Great Justice!",
            Nature: new AbilityNature.Utility(),
            Targeting: new Targeting.PointBlankAoE(Radius: 600.0, MaxTargets: 5),
            Delivery: new DeliveryMode.Instant(),
            Effects:
            [
                new AbilityEffect.ApplyStatus(new StatusEffect.Boon(BoonType.Might, DurationSeconds: 8.0, Stacks: 3), EffectTarget.Ally),
                new AbilityEffect.ApplyStatus(new StatusEffect.Boon(BoonType.Stability, DurationSeconds: 4.0, Stacks: 1), EffectTarget.Ally),
                new AbilityEffect.ApplyStatus(new StatusEffect.Boon(BoonType.Fury, DurationSeconds: 6.0, Stacks: 1), EffectTarget.Ally),
                new AbilityEffect.ApplyStatus(new StatusEffect.Boon(BoonType.Regeneration, DurationSeconds: 5.0, Stacks: 1), EffectTarget.Ally)
            ],
            CastTimeSeconds: 0.0,
            CooldownSeconds: 20.0);

        var potentIntensity = new Talent(
            Id: "potent_intensity",
            Name: "Potent Intensity (+2 Stacks to Intensity Boons)",
            Priority: 10,
            AppliesTo: a => a.GrantsBoonWithStacking(StackingMode.Intensity),
            Transform: a => a.MapBoons(
                boon => boon with { Stacks = boon.Stacks.AddFlat(2) },
                stackingFilter: StackingMode.Intensity));

        var lingeringDuration = new Talent(
            Id: "lingering_duration",
            Name: "Lingering Duration (+50% Duration to Duration Boons)",
            Priority: 10,
            AppliesTo: a => a.GrantsBoonWithStacking(StackingMode.Duration),
            Transform: a => a.MapBoons(
                boon => boon with { DurationSeconds = boon.DurationSeconds.AddPercent(0.50) },
                stackingFilter: StackingMode.Duration));

        var resolved = AbilityTalentPipeline.ExpandAbilityTree(
            heroicShout,
            new[] { potentIntensity, lingeringDuration });

        var boons = resolved.EffectiveAbility.Effects
            .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
            .Select(s => s.Status)
            .OfUnionCase<StatusEffect, StatusEffect.Boon>()
            .ToDictionary(b => b.Type);

        // Intensity Boons (+2 Stacks, Duration unchanged)
        Assert.Equal(StackingMode.Intensity, boons[BoonType.Might].Stacking);
        Assert.Equal(5, boons[BoonType.Might].Stacks.Evaluate());
        Assert.Equal(8.0, boons[BoonType.Might].DurationSeconds.Evaluate(), precision: 4);

        Assert.Equal(StackingMode.Intensity, boons[BoonType.Stability].Stacking);
        Assert.Equal(3, boons[BoonType.Stability].Stacks.Evaluate());
        Assert.Equal(4.0, boons[BoonType.Stability].DurationSeconds.Evaluate(), precision: 4);

        // Duration Boons (+50% Duration, Stacks unchanged)
        Assert.Equal(StackingMode.Duration, boons[BoonType.Fury].Stacking);
        Assert.Equal(1, boons[BoonType.Fury].Stacks.Evaluate());
        Assert.Equal(9.0, boons[BoonType.Fury].DurationSeconds.Evaluate(), precision: 4);

        Assert.Equal(StackingMode.Duration, boons[BoonType.Regeneration].Stacking);
        Assert.Equal(1, boons[BoonType.Regeneration].Stacks.Evaluate());
        Assert.Equal(7.5, boons[BoonType.Regeneration].DurationSeconds.Evaluate(), precision: 4);
    }

    [Fact]
    public void MapConditions_And_MapSoftConditions_Filter_ByType_And_StackingMode()
    {
        var jaggedStrike = new Ability(
            Id: "jagged_strike",
            Name: "Jagged Strike",
            Nature: new AbilityNature.Physical(PhysDamageType.Slashing),
            Targeting: new Targeting.SingleTarget(Range: 130.0),
            Delivery: new DeliveryMode.Melee(CleaveTargets: 3),
            Effects:
            [
                new AbilityEffect.ApplyStatus(
                    new StatusEffect.DamagingCondition(DamagingConditionType.Bleeding, TickEffect: 40.0, DurationSeconds: 6.0, TickIntervalSeconds: 1.0, Stacks: 2),
                    EffectTarget.Enemy),
                new AbilityEffect.ApplyStatus(
                    new StatusEffect.DamagingCondition(DamagingConditionType.Burning, TickEffect: 60.0, DurationSeconds: 3.0, TickIntervalSeconds: 1.0, Stacks: 1),
                    EffectTarget.Enemy),
                new AbilityEffect.ApplyStatus(
                    new StatusEffect.SoftCondition(SoftConditionType.Vulnerability, DurationSeconds: 8.0, Stacks: 5),
                    EffectTarget.Enemy),
                new AbilityEffect.ApplyStatus(
                    new StatusEffect.SoftCondition(SoftConditionType.Chilled, DurationSeconds: 4.0, Stacks: 1),
                    EffectTarget.Enemy)
            ],
            CastTimeSeconds: 0.5,
            CooldownSeconds: 8.0);

        var serratedBlades = new Talent(
            Id: "serrated_blades",
            Name: "Serrated Blades (+25% Bleeding Damage, +2s Duration)",
            Priority: 10,
            AppliesTo: a => a.AppliesCondition(DamagingConditionType.Bleeding),
            Transform: a => a.MapConditions(
                cond => cond with
                {
                    TickEffect = cond.TickEffect.AddPercent(0.25),
                    DurationSeconds = cond.DurationSeconds.AddFlat(2.0)
                },
                typeFilter: DamagingConditionType.Bleeding));

        var exposingWeakness = new Talent(
            Id: "exposing_weakness",
            Name: "Exposing Weakness (+3 Stacks to Intensity Soft Conditions)",
            Priority: 10,
            AppliesTo: a => a.AppliesSoftConditionWithStacking(StackingMode.Intensity),
            Transform: a => a.MapSoftConditions(
                cond => cond with { Stacks = cond.Stacks.AddFlat(3) },
                stackingFilter: StackingMode.Intensity));

        var resolved = AbilityTalentPipeline.ExpandAbilityTree(
            jaggedStrike,
            new[] { serratedBlades, exposingWeakness });

        var damagingConds = resolved.EffectiveAbility.Effects
            .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
            .Select(s => s.Status)
            .OfUnionCase<StatusEffect, StatusEffect.DamagingCondition>()
            .ToDictionary(c => c.Type);

        var softConds = resolved.EffectiveAbility.Effects
            .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
            .Select(s => s.Status)
            .OfUnionCase<StatusEffect, StatusEffect.SoftCondition>()
            .ToDictionary(c => c.Type);

        Assert.Equal(50.0, damagingConds[DamagingConditionType.Bleeding].TickEffect.Evaluate(), precision: 4);
        Assert.Equal(8.0, damagingConds[DamagingConditionType.Bleeding].DurationSeconds.Evaluate(), precision: 4);
        Assert.Equal(60.0, damagingConds[DamagingConditionType.Burning].TickEffect.Evaluate(), precision: 4);
        Assert.Equal(3.0, damagingConds[DamagingConditionType.Burning].DurationSeconds.Evaluate(), precision: 4);

        Assert.Equal(8, softConds[SoftConditionType.Vulnerability].Stacks.Evaluate());
        Assert.Equal(1, softConds[SoftConditionType.Chilled].Stacks.Evaluate());
    }

    [Fact]
    public void HealOverTime_Receives_HealingTalents()
    {
        var trollUnguent = new Ability(
        Id: "troll_unguent",
        Name: "Troll Unguent",
        Nature: new AbilityNature.Healing(),
        Targeting: new Targeting.Self(),
        Delivery: new DeliveryMode.Instant(),
        Effects:
        [
        new AbilityEffect.HealOverTime(
TickEffect: 145.0,
DurationSeconds: 8.0,
TickIntervalSeconds: 1.0)
        ],
        CastTimeSeconds: 0.75,
        CooldownSeconds: 25.0);
        var soothingMists = new Talent(
        Id: "soothing_mists",
        Name: "Soothing Mists (+20% Healing & +2s HoT Duration)",
        Priority: 10,
        AppliesTo: a => a.HasHealingEffect(),
        Transform: a => a.MapEffects(e => e switch
        {
            AbilityEffect.DirectHeal(var eff) =>
    new AbilityEffect.DirectHeal(eff.AddPercent(0.20)),
            AbilityEffect.HealOverTime(var tick, var dur, var interval) =>
    new AbilityEffect.HealOverTime(tick.AddPercent(0.20), dur.AddFlat(2.0), interval),
            _ => e
        }));
        var resolved = AbilityTalentPipeline.ExpandAbilityTree(trollUnguent, new[] { soothingMists });
        var hot = resolved.EffectiveAbility.Effects
        .OfUnionCase<AbilityEffect, AbilityEffect.HealOverTime>()
        .Single();
        Assert.Equal(174.0, hot.TickEffect.Evaluate(), precision: 4);
        Assert.Equal(10.0, hot.DurationSeconds.Evaluate(), precision: 4);
    }
    [Fact]
    public void RootAndComplementarySubAbilities_MeteorShower_Receives_Talents_And_SpawnsChildren()
    {
        var pyromancerPuissance = new Talent(
        Id: "pyromancer_puissance",
        Name: "+20% Fire Direct Damage",
        Priority: 10,
        AppliesTo: a => a.IsElement(ElementType.Fire) && a.HasEffect<AbilityEffect.DirectDamage>(),
        Transform: a => a.MapEffects(e => e is AbilityEffect.DirectDamage(var eff)
        ? new AbilityEffect.DirectDamage(eff.AddPercent(0.20))
        : e));
        var blastingStaff = new Talent(
        Id: "blasting_staff",
        Name: "+60 Flat & +25% AoE Radius",
        Priority: 10,
        AppliesTo: a => a.HasRadius(),
        Transform: a => a.MapRadius(r => r.AddFlat(60.0).AddPercent(0.25)));
        var mercilessControl = new Talent(
        Id: "merciless_control",
        Name: "+50% CC Duration & +50 Breakbar Damage",
        Priority: 10,
        AppliesTo: a => a.AppliesControl(),
        Transform: a => a.MapStatus<StatusEffect.Control>(cc => cc with
        {
            DurationSeconds = cc.DurationSeconds.AddPercent(0.50),
            BreakbarDamage = cc.BreakbarDamage.AddFlat(50.0)
        }));
        var ignite = new Talent(
        Id: "ignite",
        Name: "Ignite",
        Priority: 20,
        AppliesTo: a => a.IsElement(ElementType.Fire) && a.HasEffect<AbilityEffect.DirectDamage>(),
        SpawnSubAbilities: a =>
        {
            var baseTick = a.Effects
    .OfUnionCase<AbilityEffect, AbilityEffect.DirectDamage>()
    .First()
    .Effect
    .Evaluate() * 0.15;
            return
    [
    new Ability(
Id: $"{a.Id}_ignite",
Name: $"{a.Name}: Ignite",
Nature: new AbilityNature.Element(ElementType.Fire),
Targeting: new Targeting.SingleTarget(Range: 900.0),
Delivery: new DeliveryMode.Instant(),
Effects:
[
new AbilityEffect.ApplyStatus(
new StatusEffect.DamagingCondition(DamagingConditionType.Burning, baseTick, 4.0, 1.0, 1),
EffectTarget.Enemy)
],
CastTimeSeconds: 0.0,
CooldownSeconds: 0.0)
    ];
        });
        var resolved = AbilityTalentPipeline.ExpandAbilityTree(
        CreateMeteorShower(),
        new[] { pyromancerPuissance, blastingStaff, mercilessControl, ignite });
        // Root (Meteor Shower)
        var headTargeting = Assert.IsType<Targeting.GroundAoE>(resolved.EffectiveAbility.Targeting.Value);
        Assert.Equal(525.0, headTargeting.Radius.Evaluate(), precision: 4);

        var rootDamage = resolved.EffectiveAbility.Effects
        .OfUnionCase<AbilityEffect, AbilityEffect.DirectDamage>()
        .Single();
        Assert.Equal(372.0, rootDamage.Effect.Evaluate(), precision: 4);
        Assert.Empty(resolved.EffectiveAbility.SubAbilities);
        // Authored Child (Meteor Aftershock) + Talent-Spawned Child (Meteor Shower: Ignite)
        Assert.Equal(2, resolved.SubAbilities.Length);
        var aftershockNode = resolved.SubAbilities.Single(n => n.EffectiveAbility.Id == "meteor_shower_aftershock");
        var aftershockTargeting = Assert.IsType<Targeting.PointBlankAoE>(aftershockNode.EffectiveAbility.Targeting.Value);
        Assert.Equal(225.0, aftershockTargeting.Radius.Evaluate(), precision: 4);

        var controlStatus = aftershockNode.EffectiveAbility.Effects
        .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
        .Select(s => s.Status)
        .OfUnionCase<StatusEffect, StatusEffect.Control>()
        .Single();
        Assert.Equal(1.5, controlStatus.DurationSeconds.Evaluate(), precision: 4);
        Assert.Equal(150.0, controlStatus.BreakbarDamage.Evaluate(), precision: 4);
        var igniteNode = resolved.SubAbilities.Single(n => n.EffectiveAbility.Id == "meteor_shower_ignite");
        var burningStatus = igniteNode.EffectiveAbility.Effects
        .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
        .Select(s => s.Status)
        .OfUnionCase<StatusEffect, StatusEffect.DamagingCondition>()
        .Single();
        Assert.Equal(55.8, burningStatus.TickEffect.Evaluate(), precision: 4);
    }
    [Fact]
    public void MapRange_Modifies_Both_SingleTarget_And_GroundAoE()
    {
        var farReach = new Talent(
        Id: "far_reach",
        Name: "+300 Range",
        Priority: 10,
        AppliesTo: a => a.HasRange(),
        Transform: a => a.MapRange(r => r.AddFlat(300.0)));
        var fireballNode = AbilityTalentPipeline.ExpandAbilityTree(CreateFireball(), new[] { farReach });
        var meteorNode = AbilityTalentPipeline.ExpandAbilityTree(CreateMeteorShower(), new[] { farReach });
        var st = Assert.IsType<Targeting.SingleTarget>(fireballNode.EffectiveAbility.Targeting.Value);
        var aoe = Assert.IsType<Targeting.GroundAoE>(meteorNode.EffectiveAbility.Targeting.Value);

        Assert.Equal(1200.0, st.Range.Evaluate(), precision: 4);
        Assert.Equal(1500.0, aoe.Range.Evaluate(), precision: 4);
    }
    [Fact]
    public void Equipping_FireTalent_Emits_ZeroUpdates_For_PhysicalAbility_SeverArtery()
    {
        using var baseAbilities = new SourceCache<Ability, string>(a => a.Id);
        using var equippedTalents = new SourceCache<Talent, string>(t => t.Id);
        baseAbilities.AddOrUpdate(new[] { CreateFireball(), CreateMeteorShower(), CreateSeverArtery() });
        var recordedChangeSets = new List<IChangeSet<ResolvedAbilityNode, string>>();
        using var sub = AbilityTalentPipeline
        .BuildReactiveAbilityCache(baseAbilities, equippedTalents)
        .Subscribe(recordedChangeSets.Add);
        recordedChangeSets.Clear();
        var pyromancy = new Talent(
        Id: "pyromancy",
        Name: "Pyromancy (+20% Fire Damage)",
        Priority: 10,
        AppliesTo: a => a.IsElement(ElementType.Fire) && a.HasEffect<AbilityEffect.DirectDamage>(),
        Transform: a => a.MapEffects(e => e is AbilityEffect.DirectDamage(var eff)
        ? new AbilityEffect.DirectDamage(eff.AddPercent(0.20))
        : e));
        equippedTalents.AddOrUpdate(pyromancy);
        var allChanges = recordedChangeSets.SelectMany(cs => cs).ToList();
        Assert.DoesNotContain(allChanges, c => c.Key == "sever_artery");
        Assert.Equal(2, allChanges.Count);
    }
    [Fact]
    public void NoOpTalentChanges_DoNotEmitDuplicateUpdates_ForUnaffectedAbilities()
    {
        using var baseAbilities = new SourceCache<Ability, string>(a => a.Id);
        using var equippedTalents = new SourceCache<Talent, string>(t => t.Id);

        var fireball = new Ability(
            Id: "fireball",
            Name: "Fireball",
            Nature: new AbilityNature.Element(ElementType.Fire),
            Targeting: new Targeting.SingleTarget(900.0),
            Delivery: new DeliveryMode.Projectile(1200.0, 0),
            Effects: ImmutableArray.Create<AbilityEffect>(
                new AbilityEffect.DirectDamage(100.0)),
            CastTimeSeconds: 1.0,
            CooldownSeconds: 5.0);

        var frostbolt = new Ability(
            Id: "frostbolt",
            Name: "Frostbolt",
            Nature: new AbilityNature.Element(ElementType.Frost),
            Targeting: new Targeting.SingleTarget(900.0),
            Delivery: new DeliveryMode.Projectile(1100.0, 0),
            Effects: ImmutableArray.Create<AbilityEffect>(
                new AbilityEffect.DirectDamage(90.0)),
            CastTimeSeconds: 0.75,
            CooldownSeconds: 4.0);

        var piercingIce = new Talent(
            Id: "piercing_ice",
            Name: "Piercing Ice",
            Priority: 10,
            AppliesTo: a => a.Nature is AbilityNature.Element(ElementType.Frost),
            Transform: a => a with { Name = "Empowered Frostbolt" });

        baseAbilities.AddOrUpdate(fireball);
        baseAbilities.AddOrUpdate(frostbolt);

        var fireballChanges = new List<ChangeReason>();
        var frostboltChanges = new List<ChangeReason>();

        using var sub = AbilityTalentPipeline
            .BuildReactiveAbilityCache(baseAbilities, equippedTalents)
            .Subscribe(changeSet =>
            {
                foreach (var change in changeSet)
                {
                    if (change.Key == "fireball") fireballChanges.Add(change.Reason);
                    if (change.Key == "frostbolt") frostboltChanges.Add(change.Reason);
                }
            });

        // Equip and unequip a Frost-only talent
        equippedTalents.AddOrUpdate(piercingIce);
        equippedTalents.RemoveKey("piercing_ice");

        // Fireball only emitted its initial Add—zero redundant Updates
        Assert.Equal(new[] { ChangeReason.Add }, fireballChanges);

        // Frostbolt emitted Add + Update (equip) + Update (unequip)
        Assert.Equal(new[] { ChangeReason.Add, ChangeReason.Update, ChangeReason.Update }, frostboltChanges);
    }


}
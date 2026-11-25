using System;
using System.Collections.Generic;
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
        Effects: [],
        CastTimeSeconds: 3.25,
        CooldownSeconds: 30.0,
        SubAbilities:
        [
            new Ability(
                Id: "meteor_shower_impact",
                Name: "Meteor Impact",
                Nature: new AbilityNature.Element(ElementType.Fire),
                Targeting: new Targeting.PointBlankAoE(Radius: 120.0, MaxTargets: 5),
                Delivery: new DeliveryMode.Blast(new ComboFinisher.Blast()),
                Effects:
                [
                    new AbilityEffect.DirectDamage(310.0),
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
    public void AuthoredSubAbilities_MeteorShower_RecursivelyReceives_Talents_And_SpawnsChildren()
    {
        // Arrange: Talents apply to both Meteor Shower (head) and Meteor Impact (authored child)
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
                            new StatusEffect.DamagingCondition(DamagingConditionType.Burning, 40.0, 4.0, 1.0, 1),
                            EffectTarget.Enemy)
                    ],
                    CastTimeSeconds: 0.0,
                    CooldownSeconds: 0.0)
            ]);

        // Act
        var resolved = AbilityTalentPipeline.ExpandAbilityTree(
            CreateMeteorShower(),
            new[] { blastingStaff, mercilessControl, ignite });

        // Assert Head (Meteor Shower): Radius (360 + 60) * 1.25 = 525
        var headTargeting = Assert.IsType<Targeting.GroundAoE>(resolved.EffectiveAbility.Targeting);
        Assert.Equal(525.0, headTargeting.Radius.Evaluate(), precision: 4);
        Assert.Empty(resolved.EffectiveAbility.SubAbilities); // Normalized out of raw record

        // Assert Authored Child (Meteor Impact): Radius (120 + 60) * 1.25 = 225, CC Duration 1.5s, Breakbar 150
        var impactNode = Assert.Single(resolved.SubAbilities);
        Assert.Equal("meteor_shower_impact", impactNode.EffectiveAbility.Id);

        var impactTargeting = Assert.IsType<Targeting.PointBlankAoE>(impactNode.EffectiveAbility.Targeting);
        Assert.Equal(225.0, impactTargeting.Radius.Evaluate(), precision: 4);

        var controlStatus = impactNode.EffectiveAbility.Effects
            .OfUnionCase<AbilityEffect, AbilityEffect.ApplyStatus>()
            .Select(s => s.Status)
            .OfUnionCase<StatusEffect, StatusEffect.Control>()
            .Single();


        Assert.Equal(1.5, controlStatus.DurationSeconds.Evaluate(), precision: 4);
        Assert.Equal(150.0, controlStatus.BreakbarDamage.Evaluate(), precision: 4);

        // Assert Grandchild spawned by Ignite on Meteor Impact
        var igniteChild = Assert.Single(impactNode.SubAbilities);
        Assert.Equal("meteor_shower_impact_ignite", igniteChild.EffectiveAbility.Id);
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

        var st = Assert.IsType<Targeting.SingleTarget>(fireballNode.EffectiveAbility.Targeting);
        var aoe = Assert.IsType<Targeting.GroundAoE>(meteorNode.EffectiveAbility.Targeting);

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
}

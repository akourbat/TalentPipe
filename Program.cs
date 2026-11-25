using System;
using System.Linq;
using DynamicData;
using SpellTalentSystem;

using var baseAbilities = new SourceCache<Ability, string>(a => a.Id);
using var equippedTalents = new SourceCache<Talent, string>(t => t.Id);

using var subscription = AbilityTalentPipeline
    .BuildReactiveAbilityCache(baseAbilities, equippedTalents)
    .Subscribe(changes =>
    {
        Console.WriteLine($"=== Talent Enrichment Update ({changes.Count} changes) ===");
        foreach (var change in changes)
        {
            Console.WriteLine($"  [{change.Reason}] {change.Key}");
            if (change.Reason is ChangeReason.Add or ChangeReason.Update)
            {
                PrintNode(change.Current, indent: 2);
            }
        }
        Console.WriteLine();
    });

// 1. Populate GW2-style Base Abilities
baseAbilities.AddOrUpdate(new Ability[]
{
    new Ability(
        Id: "fireball",
        Name: "Fireball",
        Nature: new AbilityNature.Element(ElementType.Fire),
        Targeting: new Targeting.SingleTarget(Range: 900.0),
        Delivery: new DeliveryMode.Projectile(Speed: 1200.0, PierceCount: 0, Finisher: new ComboFinisher.Projectile(0.20)),
        Effects: [new AbilityEffect.DirectDamage(250.0)],
        CastTimeSeconds: 0.75,
        CooldownSeconds: 0.0),

    new Ability(
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
        ]),

    new Ability(
        Id: "shocking_aura",
        Name: "Shocking Aura",
        Nature: new AbilityNature.Element(ElementType.Lightning),
        Targeting: new Targeting.Self(),
        Delivery: new DeliveryMode.Instant(),
        Effects:
        [
            new AbilityEffect.ApplyStatus(
                new StatusEffect.Aura(AuraType.Shocking, DurationSeconds: 4.0),
                EffectTarget.Self)
        ],
        CastTimeSeconds: 0.0,
        CooldownSeconds: 25.0),

    new Ability(
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
        CooldownSeconds: 0.0)
});

// 2. Define Talents
var pyromancerPuissance = new Talent(
    Id: "pyromancer_puissance",
    Name: "Pyromancer's Puissance (+20% Fire Direct Damage)",
    Priority: 10,
    AppliesTo: a => a.IsElement(ElementType.Fire) && a.HasEffect<AbilityEffect.DirectDamage>(),
    Transform: a => a.MapEffects(e => e is AbilityEffect.DirectDamage(var eff)
        ? new AbilityEffect.DirectDamage(eff.AddPercent(0.20))
        : e));

var blastingStaff = new Talent(
    Id: "blasting_staff",
    Name: "Blasting Staff (+60 Flat & +25% AoE Radius)",
    Priority: 10,
    AppliesTo: a => a.HasRadius(),
    Transform: a => a.MapRadius(r => r.AddFlat(60.0).AddPercent(0.25)));

var mercilessControl = new Talent(
    Id: "merciless_control",
    Name: "Merciless Control (+50% CC Duration, +50 Breakbar Damage)",
    Priority: 10,
    AppliesTo: a => a.AppliesControl(),
    Transform: a => a.MapStatus<StatusEffect.Control>(cc => cc with
    {
        DurationSeconds = cc.DurationSeconds.AddPercent(0.50),
        BreakbarDamage = cc.BreakbarDamage.AddFlat(50.0)
    }));

var powerfulAura = new Talent(
    Id: "powerful_aura",
    Name: "Powerful Aura (Auras share to 5 allies in 360r and grant Fury)",
    Priority: 15,
    AppliesTo: a => a.GrantsAura(),
    Transform: a => a
        .MapTargeting(_ => new Targeting.PointBlankAoE(Radius: 360.0, MaxTargets: 5))
        .MapEffects(e => e is AbilityEffect.ApplyStatus(StatusEffect.Aura aura, _)
            ? new AbilityEffect.ApplyStatus(aura, EffectTarget.Ally)
            : e)
        .AddEffect(new AbilityEffect.ApplyStatus(
            new StatusEffect.Boon(BoonType.Fury, DurationSeconds: 5.0, Stacks: 1),
            EffectTarget.Ally)));

var ignite = new Talent(
    Id: "ignite",
    Name: "Ignite (Fire Direct Damage spawns Burning sub-ability)",
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
                        new StatusEffect.DamagingCondition(
                            DamagingConditionType.Burning,
                            TickEffect: baseTick,
                            DurationSeconds: 4.0,
                            TickIntervalSeconds: 1.0,
                            Stacks: 1),
                        EffectTarget.Enemy)
                ],
                CastTimeSeconds: 0.0,
                CooldownSeconds: 0.0)
        ];
    });

Console.WriteLine(">> Equipping Fire, AoE, Control, and Aura talents (Sever Artery emits NO update)...");
equippedTalents.AddOrUpdate(new[]
{
    pyromancerPuissance,
    blastingStaff,
    mercilessControl,
    powerfulAura,
    ignite
});

static void PrintNode(ResolvedAbilityNode node, int indent)
{
    var pad = new string(' ', indent * 2);
    var a = node.EffectiveAbility;

    // Relies on `public override string ToString() => Value?.ToString() ?? string.Empty;` on the unions
    Console.WriteLine($"{pad}- [{a.Id}] {a.Name} | {a.Targeting} | Effects=[{string.Join("; ", a.Effects)}]");
    if (node.AppliedTalentIds.Length > 0)
    {
        Console.WriteLine($"{pad}  Talents: {string.Join(", ", node.AppliedTalentIds)}");
    }

    foreach (var sub in node.SubAbilities)
    {
        PrintNode(sub, indent + 1);
    }
}

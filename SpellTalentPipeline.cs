using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using DynamicData;

namespace SpellTalentSystem;

public static class AbilityTalentPipeline
{
    /// <summary>
    /// Applies matching talents in ascending Priority order so earlier mutations
    /// (e.g., element conversion) are visible to later talents in the same pass.
    /// </summary>
    public static (Ability Effective, ImmutableArray<string> AppliedIds) ComputeEffectiveAbility(
        Ability baseAbility,
        IEnumerable<Talent> equippedTalents)
    {
        var current = baseAbility;
        var applied = ImmutableArray.CreateBuilder<string>();

        var orderedTalents = equippedTalents
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.Id, StringComparer.Ordinal);

        foreach (var talent in orderedTalents)
        {
            if (talent.Transform is not null && talent.AppliesTo(current))
            {
                var next = talent.Transform(current);
                if (!ReferenceEquals(next, current) && !Equals(next, current))
                {
                    current = next;
                    applied.Add(talent.Id);
                }
            }
        }

        return (current, applied.ToImmutable());
    }

    /// <summary>
    /// Recursively computes the effective ability and expands both:
    /// 1) Authored child sub-abilities attached to <see cref="Ability.SubAbilities"/> (e.g., Meteor Shower -> Meteor Impact)
    /// 2) Dynamic sub-abilities spawned by matching talents (<see cref="Talent.SpawnSubAbilities"/>)
    /// Includes cycle protection via ancestor ID tracking.
    /// </summary>
    public static ResolvedAbilityNode ExpandAbilityTree(
        Ability baseAbility,
        IReadOnlyList<Talent> equippedTalents,
        ImmutableHashSet<string>? visitedIds = null)
    {
        visitedIds ??= ImmutableHashSet<string>.Empty;

        var (effective, appliedTransforms) = ComputeEffectiveAbility(baseAbility, equippedTalents);
        var appliedAll = appliedTransforms.ToBuilder();
        var nextVisited = visitedIds.Add(effective.Id);

        var subNodes = ImmutableArray.CreateBuilder<ResolvedAbilityNode>();

        // 1. Expand authored dependent sub-abilities (e.g., Meteor Impact, secondary chain hits)
        foreach (var authoredChild in effective.SubAbilities)
        {
            if (nextVisited.Contains(authoredChild.Id))
                continue;

            subNodes.Add(ExpandAbilityTree(authoredChild, equippedTalents, nextVisited));
        }

        // 2. Expand talent-spawned sub-abilities
        var spawners = equippedTalents
            .Where(t => t.SpawnSubAbilities is not null)
            .OrderBy(t => t.Priority)
            .ThenBy(t => t.Id, StringComparer.Ordinal);

        foreach (var talent in spawners)
        {
            if (!talent.AppliesTo(effective))
                continue;

            if (!appliedAll.Contains(talent.Id))
                appliedAll.Add(talent.Id);

            foreach (var spawnedBase in talent.SpawnSubAbilities!(effective))
            {
                if (nextVisited.Contains(spawnedBase.Id))
                    continue;

                subNodes.Add(ExpandAbilityTree(spawnedBase, equippedTalents, nextVisited));
            }
        }

        // Clear raw authored SubAbilities on EffectiveAbility so the resolved tree
        // holds the enriched children exclusively in ResolvedAbilityNode.SubAbilities.
        var normalizedEffective = effective with { SubAbilities = ImmutableArray<Ability>.Empty };

        return new ResolvedAbilityNode(
            normalizedEffective,
            appliedAll.ToImmutable(),
            subNodes.ToImmutable());
    }

    /// <summary>
    /// Reactive pipeline using AutoRefreshOnObservable + Transform(transformOnRefresh: true)
    /// and structural equality filtering so unchanged abilities emit zero ChangeReason.Update events.
    /// </summary>
    public static IObservable<IChangeSet<ResolvedAbilityNode, string>> BuildReactiveAbilityCache(
        ISourceCache<Ability, string> baseAbilities,
        ISourceCache<Talent, string> equippedTalents)
    {
        var latestTalents = Array.Empty<Talent>();

        var talentChanged = equippedTalents
            .Connect()
            .QueryWhenChanged(query => query.Items.ToArray())
            .Do(snapshot => Volatile.Write(ref latestTalents, snapshot))
            .Publish()
            .RefCount();

        return baseAbilities
            .Connect()
            .AutoRefreshOnObservable(_ => talentChanged)
            .Transform(
                ability => ExpandAbilityTree(ability, Volatile.Read(ref latestTalents)),
                transformOnRefresh: true)
            .Select(changeSet => (IChangeSet<ResolvedAbilityNode, string>)new ChangeSet<ResolvedAbilityNode, string>(
                changeSet.Where(change =>
                    change.Reason != ChangeReason.Update ||
                    !change.Previous.HasValue ||
                    !Equals(change.Previous.Value, change.Current))))
            .Where(changeSet => changeSet.Count > 0);
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;

namespace SpellTalentSystem;

// ============================================================================
// STAGE 1: REACTIVE ABILITY + TALENT PIPELINE (DynamicData)
// ============================================================================

public static class AbilityTalentPipeline
{
    private static readonly IComparer<Talent> TalentOrderComparer =
        Comparer<Talent>.Create((a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

    /// <summary>
    /// Creates a live, incremental DynamicData stream of talent-resolved ability trees.
    /// </summary>
    public static IObservable<IChangeSet<ResolvedAbilityNode, string>> BuildReactiveAbilityCache(
        IObservableCache<Ability, string> baseAbilities,
        IObservableCache<Talent, string> equippedTalents)
    {
        ArgumentNullException.ThrowIfNull(baseAbilities);
        ArgumentNullException.ThrowIfNull(equippedTalents);

        var sortedTalents = equippedTalents
            .Connect()
            .QueryWhenChanged(query =>
            {
                if (query.Count == 0)
                {
                    return Array.Empty<Talent>();
                }

                var array = query.Items.ToArray();
                Array.Sort(array, TalentOrderComparer);
                return array;
            })
            .StartWith(Array.Empty<Talent>())
            .Replay(1)
            .RefCount();

        return baseAbilities
            .Connect()
            .TransformOnObservable((baseAbility, _) =>
                sortedTalents
                    .Select(talents => ExpandAbilityTree(baseAbility, talents))
                    .DistinctUntilChanged());
    }

    /// <summary>
    /// Recursively applies all matching talents to <paramref name="root"/> and expands both
    /// authored <see cref="Ability.SubAbilities"/> and talent-spawned sub-abilities into a
    /// tree of <see cref="ResolvedAbilityNode"/>s, with cycle protection via visited IDs.
    /// </summary>
    public static ResolvedAbilityNode ExpandAbilityTree(
        Ability root,
        IReadOnlyList<Talent> orderedTalents,
        HashSet<string>? visited = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(orderedTalents);

        visited ??= new HashSet<string>(StringComparer.Ordinal);
        visited.Add(root.Id);

        var current = root;
        var appliedTalentIds = ImmutableArray.CreateBuilder<string>();
        var spawnedChildren = ImmutableArray.CreateBuilder<Ability>();

        for (int i = 0; i < orderedTalents.Count; i++)
        {
            var talent = orderedTalents[i];
            if (!talent.AppliesTo(current))
            {
                continue;
            }

            appliedTalentIds.Add(talent.Id);

            if (talent.Transform is { } transform)
            {
                current = transform(current);
            }

            if (talent.SpawnSubAbilities is { } spawn)
            {
                foreach (var spawned in spawn(current))
                {
                    spawnedChildren.Add(spawned);
                }
            }
        }

        var resolvedChildren = ImmutableArray.CreateBuilder<ResolvedAbilityNode>(
            current.SubAbilities.Length + spawnedChildren.Count);

        for (int i = 0; i < current.SubAbilities.Length; i++)
        {
            var child = current.SubAbilities[i];
            if (visited.Add(child.Id))
            {
                resolvedChildren.Add(ExpandAbilityTree(child, orderedTalents, visited));
            }
        }

        for (int i = 0; i < spawnedChildren.Count; i++)
        {
            var child = spawnedChildren[i];
            if (visited.Add(child.Id))
            {
                resolvedChildren.Add(ExpandAbilityTree(child, orderedTalents, visited));
            }
        }

        var effectiveRoot = current.SubAbilities.IsEmpty
            ? current
            : current with { SubAbilities = ImmutableArray<Ability>.Empty };

        return new ResolvedAbilityNode(
            EffectiveAbility: effectiveRoot,
            AppliedTalentIds: appliedTalentIds.ToImmutable(),
            SubAbilities: resolvedChildren.ToImmutable());
    }
}

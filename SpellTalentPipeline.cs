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
    /// <summary>
    /// Reusable, allocation-free comparer that defines the canonical execution order
    /// for equipped talents:
    /// 1. Ascending <see cref="Talent.Priority"/> (e.g., flat additions at Priority 10
    ///    before percentage multipliers or conversions at Priority 20).
    /// 2. Ordinal <see cref="Talent.Id"/> tie-breaker so two talents with equal priority
    ///    always apply in a deterministic, reproducible sequence across runs.
    /// </summary>
    private static readonly IComparer<Talent> TalentOrderComparer =
        Comparer<Talent>.Create((a, b) =>
        {
            int cmp = a.Priority.CompareTo(b.Priority);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });

    /// <summary>
    /// Builds a live, incremental DynamicData change-set stream that resolves each base
    /// <see cref="Ability"/> in <paramref name="baseAbilities"/> against all currently
    /// equipped <see cref="Talent"/>s in <paramref name="equippedTalents"/>.
    /// </summary>
    /// <param name="baseAbilities">
    /// Source cache of base ability definitions keyed by <see cref="Ability.Id"/>.
    /// </param>
    /// <param name="equippedTalents">
    /// Source cache of currently equipped talents keyed by <see cref="Talent.Id"/>.
    /// </param>
    /// <returns>
    /// An observable stream of DynamicData <see cref="IChangeSet{TObject, TKey}"/>
    /// containing <see cref="ResolvedAbilityNode"/> trees keyed by root ability ID.
    /// </returns>
    public static IObservable<IChangeSet<ResolvedAbilityNode, string>> BuildReactiveAbilityCache(
        IObservableCache<Ability, string> baseAbilities,
        IObservableCache<Talent, string> equippedTalents)
    {
        ArgumentNullException.ThrowIfNull(baseAbilities);
        ArgumentNullException.ThrowIfNull(equippedTalents);

        // STEP 1: Build a single shared observable of the sorted talent array.
        // - Connect() subscribes to changes in equippedTalents.
        // - QueryWhenChanged(...) runs once per change-set batch (not per item), giving us
        //   a point-in-time snapshot of all currently equipped talents.
        // - Array.Sort(...) sorts a single contiguous array in-place using our static comparer,
        //   avoiding LINQ OrderBy/ThenBy iterator & state-machine allocations.
        // - StartWith(Array.Empty<Talent>()) guarantees downstream abilities emit immediately
        //   on subscription even when zero talents are equipped yet.
        // - Replay(1).RefCount() multicasts the latest sorted snapshot to every ability stream
        //   so equipping a talent sorts the talent array ONCE instead of N times (for N abilities),
        //   and automatically disposes the upstream subscription when the last observer unsubscribes.
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

        // STEP 2: Project each base ability into a reactive ResolvedAbilityNode tree.
        // - TransformOnObservable creates an inner observable per ability key:
        //     * When a single baseAbility updates, ONLY that ability's inner observable rebuilds.
        //     * When sortedTalents emits a new talent array, every ability re-evaluates
        //       ExpandAbilityTree(baseAbility, talents).
        // - DistinctUntilChanged() compares the newly resolved tree against the previous emission
        //   for that specific ability using record structural equality. If a newly equipped talent
        //   does not affect that ability (e.g., a Fire talent equipped while tracking a Physical
        //   sword strike), DistinctUntilChanged suppresses the duplicate emission so downstream
        //   consumers (UI, Stage 2 Attribute Pipeline, combat log) receive zero wasted updates.
        return baseAbilities
            .Connect()
            .TransformOnObservable((baseAbility, _) =>
                sortedTalents
                    .Select(talents => ExpandAbilityTree(baseAbility, talents))
                    .DistinctUntilChanged());
    }

    /// <summary>
    /// Pure, deterministic tree resolver that applies matching talents to <paramref name="root"/>
    /// and recursively expands both authored <see cref="Ability.SubAbilities"/> and
    /// talent-spawned sub-abilities into a normalized <see cref="ResolvedAbilityNode"/> hierarchy.
    /// </summary>
    /// <param name="root">The ability node currently being resolved.</param>
    /// <param name="orderedTalents">
    /// Talents pre-sorted by ascending <see cref="Talent.Priority"/> and <see cref="Talent.Id"/>.
    /// </param>
    /// <param name="visited">
    /// Tracks ability IDs already encountered along the current traversal to prevent infinite
    /// recursion if a sub-ability or talent spawner introduces a cycle.
    /// </param>
    public static ResolvedAbilityNode ExpandAbilityTree(
        Ability root,
        IReadOnlyList<Talent> orderedTalents,
        HashSet<string>? visited = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(orderedTalents);

        // Initialize cycle-detection set at the root call and mark the current ability ID as visited.
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        visited.Add(root.Id);

        var current = root;
        var appliedTalentIds = ImmutableArray.CreateBuilder<string>();
        var spawnedChildren = ImmutableArray.CreateBuilder<Ability>();

        // STEP 1: Apply all matching talents in deterministic priority order.
        // Note that we evaluate talent.AppliesTo(current) against the *evolving* `current`
        // ability—so if a high-priority talent converts a Physical skill into Fire damage,
        // a lower-priority "Fire abilities deal +20% damage" talent will naturally match it!
        for (int i = 0; i < orderedTalents.Count; i++)
        {
            var talent = orderedTalents[i];
            if (!talent.AppliesTo(current))
            {
                continue;
            }

            // Record that this talent matched (useful for UI tooltips, audit trails, and debugging).
            appliedTalentIds.Add(talent.Id);

            // Mutate ability properties (damage, radius, boons, cooldown, etc.) if defined.
            if (talent.Transform is { } transform)
            {
                current = transform(current);
            }

            // Collect any proc/follow-up sub-abilities spawned by this talent (e.g., "Ignite"),
            // passing the transformed `current` so spawned skills can scale off modified stats.
            if (talent.SpawnSubAbilities is { } spawn)
            {
                foreach (var spawned in spawn(current))
                {
                    spawnedChildren.Add(spawned);
                }
            }
        }

        // STEP 2: Recursively resolve all child abilities (authored first, then talent-spawned).
        // Each child goes through the full ExpandAbilityTree pipeline so nested abilities
        // (such as "Meteor Aftershock" or "Meteor Shower: Ignite") also receive matching talents.
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

        // STEP 3: Normalize the resolved node.
        // We clear `SubAbilities` on `EffectiveAbility` because all authored and spawned children
        // now live uniformly in `ResolvedAbilityNode.SubAbilities` as fully resolved tree nodes.
        // Avoiding the `with` allocation when `SubAbilities` is already empty keeps common
        // single-node abilities allocation-light.
        var effectiveRoot = current.SubAbilities.IsEmpty
            ? current
            : current with { SubAbilities = ImmutableArray<Ability>.Empty };

        return new ResolvedAbilityNode(
            EffectiveAbility: effectiveRoot,
            AppliedTalentIds: appliedTalentIds.ToImmutable(),
            SubAbilities: resolvedChildren.ToImmutable());
    }
}

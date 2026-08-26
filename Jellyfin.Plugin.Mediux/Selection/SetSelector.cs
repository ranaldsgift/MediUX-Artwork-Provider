namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Selects MediUX sets using creator priority, completeness, and popularity.
/// </summary>
public static class SetSelector
{
    /// <summary>
    /// Filters sets by excluded authors and optional priority-only mode.
    /// Excluded authors always win over priority list membership.
    /// </summary>
    public static IReadOnlyList<MediuxSet> FilterSets(
        IReadOnlyList<MediuxSet> sets,
        IReadOnlyList<string> priorityCreators,
        IReadOnlyList<string> excludedCreators,
        bool onlyPrioritizedAuthors)
    {
        if (sets.Count == 0)
        {
            return [];
        }

        var excluded = new HashSet<string>(
            excludedCreators.Where(static s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);

        IEnumerable<MediuxSet> filtered = sets.Where(s => !excluded.Contains(s.Username));

        if (onlyPrioritizedAuthors)
        {
            if (priorityCreators.Count == 0)
            {
                return [];
            }

            var allowed = new HashSet<string>(
                priorityCreators.Where(static s => !string.IsNullOrWhiteSpace(s)),
                StringComparer.OrdinalIgnoreCase);

            filtered = filtered.Where(s => allowed.Contains(s.Username));
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Selects preferred images for the given needs from available sets,
    /// then includes all other matching assets as alternatives.
    /// </summary>
    /// <param name="sets">Available sets (already author-filtered).</param>
    /// <param name="needs">Needed image slots.</param>
    /// <param name="priorityCreators">Ordered creator usernames (highest first).</param>
    /// <param name="bindings">Optional sticky set bindings by category.</param>
    /// <returns>Selection result.</returns>
    public static SelectionResult Select(
        IReadOnlyList<MediuxSet> sets,
        IReadOnlyList<ImageSlot> needs,
        IReadOnlyList<string> priorityCreators,
        SetBindings? bindings = null)
    {
        if (sets.Count == 0 || needs.Count == 0)
        {
            return new SelectionResult();
        }

        var setsById = sets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var filled = new Dictionary<ImageSlot, SelectedImage>();
        var usedSetIds = new HashSet<string>(StringComparer.Ordinal);
        var updatedBindings = new Dictionary<SetBindingKind, string>();

        if (bindings is not null)
        {
            ApplyStickyBindings(setsById, needs, bindings, filled, usedSetIds, updatedBindings);
        }

        var remainingNeeds = needs.Where(n => !filled.ContainsKey(n)).ToList();

        if (remainingNeeds.Count > 0 && priorityCreators.Count > 0)
        {
            foreach (var creator in priorityCreators)
            {
                var creatorSets = sets
                    .Where(s => string.Equals(s.Username, creator, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (creatorSets.Count == 0)
                {
                    continue;
                }

                var best = OrderByCompleteness(creatorSets, remainingNeeds).First();
                if (Score(best, remainingNeeds) <= 0)
                {
                    continue;
                }

                AssignFromSet(best, remainingNeeds, filled);
                usedSetIds.Add(best.Id);
                RecordBindingsFromAssignment(best, remainingNeeds, filled, updatedBindings);
                break;
            }
        }

        remainingNeeds = needs.Where(n => !filled.ContainsKey(n)).ToList();

        if (remainingNeeds.Count > 0 && filled.Count == 0)
        {
            var best = OrderByCompleteness(sets, remainingNeeds).FirstOrDefault();
            if (best is not null && Score(best, remainingNeeds) > 0)
            {
                AssignFromSet(best, remainingNeeds, filled);
                usedSetIds.Add(best.Id);
                RecordBindingsFromAssignment(best, remainingNeeds, filled, updatedBindings);
            }
        }

        remainingNeeds = needs.Where(n => !filled.ContainsKey(n)).ToList();

        var guard = 0;
        while (remainingNeeds.Count > 0 && guard++ < sets.Count + 2)
        {
            var candidate = OrderByCompleteness(sets.Where(s => !usedSetIds.Contains(s.Id)), remainingNeeds)
                .FirstOrDefault(s => Score(s, remainingNeeds) > 0);

            if (candidate is null)
            {
                candidate = OrderByCompleteness(sets, remainingNeeds).FirstOrDefault(s => Score(s, remainingNeeds) > 0);
                if (candidate is null)
                {
                    break;
                }
            }

            var before = filled.Count;
            AssignFromSet(candidate, remainingNeeds, filled);
            usedSetIds.Add(candidate.Id);
            RecordBindingsFromAssignment(candidate, remainingNeeds, filled, updatedBindings);
            if (filled.Count == before)
            {
                break;
            }

            remainingNeeds = needs.Where(n => !filled.ContainsKey(n)).ToList();
        }

        var preferred = OrderPreferred(filled.Values.ToList(), needs);

        var preferredAssetIds = new HashSet<string>(
            preferred.Select(p => p.Image.AssetId),
            StringComparer.OrdinalIgnoreCase);

        var alternatives = sets
            .SelectMany(set => set.Images.Select(img => new SelectedImage
            {
                Image = img,
                SourceSet = set,
                IsPreferred = false
            }))
            .Where(s => !preferredAssetIds.Contains(s.Image.AssetId))
            .GroupBy(s => s.Image.AssetId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.SourceSet.EffectivePopularity).First())
            .OrderByDescending(s => s.SourceSet.EffectivePopularity)
            .ThenByDescending(s => s.SourceSet.DateUpdated)
            .ToArray();

        return new SelectionResult
        {
            Preferred = preferred,
            Alternatives = alternatives,
            BindingUpdates = updatedBindings
        };
    }

    /// <summary>
    /// Orders sets for the set browser: priority creators first (all sets per creator),
    /// then remaining sets by artwork count.
    /// </summary>
    public static List<MediuxSet> OrderSetsForBrowser(
        IReadOnlyList<MediuxSet> sets,
        IReadOnlyList<string> priorityCreators)
    {
        if (sets.Count == 0)
        {
            return [];
        }

        var result = new List<MediuxSet>(sets.Count);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var creator in priorityCreators)
        {
            if (string.IsNullOrWhiteSpace(creator))
            {
                continue;
            }

            var creatorSets = sets
                .Where(s => !usedIds.Contains(s.Id)
                    && string.Equals(s.Username, creator, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Images.Count)
                .ThenByDescending(s => s.EffectivePopularity)
                .ThenByDescending(s => s.DateUpdated)
                .ToList();

            foreach (var set in creatorSets)
            {
                result.Add(set);
                usedIds.Add(set.Id);
            }
        }

        var unmatched = sets
            .Where(s => !usedIds.Contains(s.Id))
            .OrderByDescending(s => s.Images.Count)
            .ThenByDescending(s => s.EffectivePopularity)
            .ThenByDescending(s => s.DateUpdated);

        result.AddRange(unmatched);
        return result;
    }

    /// <summary>
    /// Scores how many needed slots a set can fill.
    /// </summary>
    public static int Score(MediuxSet set, IReadOnlyList<ImageSlot> needs)
    {
        var provided = new HashSet<ImageSlot>(set.Images.Select(i => i.Slot));
        return needs.Count(provided.Contains);
    }

    /// <summary>
    /// Maps an image slot to a sticky binding category.
    /// </summary>
    public static SetBindingKind? GetBindingKind(ImageSlot slot)
        => slot.Kind switch
        {
            ImageSlotKind.Primary => SetBindingKind.Poster,
            ImageSlotKind.Backdrop => SetBindingKind.Backdrop,
            ImageSlotKind.Logo => SetBindingKind.Logo,
            ImageSlotKind.AlbumArt => SetBindingKind.AlbumArt,
            ImageSlotKind.EpisodeTitleCard => SetBindingKind.Titlecards,
            ImageSlotKind.SeasonPrimary when slot.SeasonNumber == 0 => SetBindingKind.SpecialsPoster,
            ImageSlotKind.SeasonPrimary => SetBindingKind.SeasonPosters,
            _ => null
        };

    private static void ApplyStickyBindings(
        IReadOnlyDictionary<string, MediuxSet> setsById,
        IReadOnlyList<ImageSlot> needs,
        SetBindings bindings,
        IDictionary<ImageSlot, SelectedImage> filled,
        HashSet<string> usedSetIds,
        IDictionary<SetBindingKind, string> updatedBindings)
    {
        foreach (var need in needs)
        {
            if (filled.ContainsKey(need))
            {
                continue;
            }

            var kind = GetBindingKind(need);
            if (kind is null)
            {
                continue;
            }

            var setId = bindings.Get(kind.Value);
            if (string.IsNullOrWhiteSpace(setId) || !setsById.TryGetValue(setId, out var set))
            {
                continue;
            }

            var match = set.Images.FirstOrDefault(i => i.Slot.Equals(need));
            if (match is null)
            {
                continue;
            }

            filled[need] = new SelectedImage
            {
                Image = match,
                SourceSet = set,
                IsPreferred = true
            };
            usedSetIds.Add(set.Id);
            updatedBindings[kind.Value] = set.Id;
        }
    }

    private static void RecordBindingsFromAssignment(
        MediuxSet set,
        IReadOnlyList<ImageSlot> attemptedNeeds,
        IDictionary<ImageSlot, SelectedImage> filled,
        IDictionary<SetBindingKind, string> updatedBindings)
    {
        foreach (var need in attemptedNeeds)
        {
            if (!filled.TryGetValue(need, out var selected) || selected.SourceSet.Id != set.Id)
            {
                continue;
            }

            var kind = GetBindingKind(need);
            if (kind is null || updatedBindings.ContainsKey(kind.Value))
            {
                continue;
            }

            updatedBindings[kind.Value] = set.Id;
        }
    }

    private static IOrderedEnumerable<MediuxSet> OrderByCompleteness(
        IEnumerable<MediuxSet> sets,
        IReadOnlyList<ImageSlot> needs)
    {
        return sets
            .OrderByDescending(s => Score(s, needs))
            .ThenByDescending(s => s.Images.Count)
            .ThenByDescending(s => s.EffectivePopularity)
            .ThenByDescending(s => s.DateUpdated);
    }

    private static void AssignFromSet(
        MediuxSet set,
        IReadOnlyList<ImageSlot> needs,
        IDictionary<ImageSlot, SelectedImage> filled)
    {
        foreach (var need in needs)
        {
            if (filled.ContainsKey(need))
            {
                continue;
            }

            var match = set.Images.FirstOrDefault(i => i.Slot.Equals(need));
            if (match is null)
            {
                continue;
            }

            filled[need] = new SelectedImage
            {
                Image = match,
                SourceSet = set,
                IsPreferred = true
            };
        }
    }

    private static List<SelectedImage> OrderPreferred(
        List<SelectedImage> selected,
        IReadOnlyList<ImageSlot> needs)
    {
        var order = needs
            .Select((slot, index) => (slot, index))
            .ToDictionary(x => x.slot, x => x.index);

        return selected
            .OrderBy(s => order.TryGetValue(s.Image.Slot, out var i) ? i : int.MaxValue)
            .ToList();
    }
}

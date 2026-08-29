using Jellyfin.Plugin.Mediux.Configuration;

namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Selects MediUX sets using creator priority, need-coverage ranking, and sticky bindings.
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
    /// Binding updates are wanted sets only — gap-fill does not change bindings.
    /// </summary>
    public static SelectionResult Select(
        IReadOnlyList<MediuxSet> sets,
        IReadOnlyList<ImageSlot> needs,
        IReadOnlyList<string> priorityCreators,
        SetBindings? bindings = null,
        PluginConfiguration? config = null)
    {
        if (sets.Count == 0 || needs.Count == 0)
        {
            return new SelectionResult();
        }

        var setsById = sets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var filled = new Dictionary<ImageSlot, SelectedImage>();

        var ranked = SetRanker.RankSets(sets, needs, priorityCreators);
        var wantedByKind = SetRanker.AssignWantedSets(
            ranked,
            needs,
            bindings,
            setsById);

        foreach (var (kind, set) in wantedByKind)
        {
            var kindNeeds = needs
                .Where(n => GetBindingKind(n) == kind && !filled.ContainsKey(n))
                .ToList();
            AssignFromSet(set, kindNeeds, filled);
        }

        foreach (var need in needs.Where(n => !filled.ContainsKey(n)))
        {
            var kind = GetBindingKind(need);
            if (kind is null)
            {
                continue;
            }

            var binding = bindings?.Get(kind.Value);
            var picked = PickImageForSlot(sets, need, binding, priorityCreators);
            if (picked is not null)
            {
                filled[need] = picked;
            }
        }

        var bindingUpdates = new Dictionary<SetBindingKind, ImageTypeBinding>();
        foreach (var (kind, set) in wantedByKind)
        {
            var kindNeeds = needs.Where(n => GetBindingKind(n) == kind).ToList();
            var existing = bindings?.Get(kind);
            bindingUpdates[kind] = new ImageTypeBinding
            {
                Set = set.Id,
                Author = set.Username,
                Locked = existing?.Locked == true,
                Missing = SetRanker.ComputeMissing(set, kind, kindNeeds)
            };
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
            BindingUpdates = bindingUpdates
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
    /// Picks an image for a single slot: bound set first, then highest-ranked set with the slot.
    /// </summary>
    public static SelectedImage? PickImageForSlot(
        IReadOnlyList<MediuxSet> sets,
        ImageSlot slot,
        ImageTypeBinding? binding,
        IReadOnlyList<string> priorityCreators)
    {
        if (sets.Count == 0)
        {
            return null;
        }

        if (binding?.Set is not null)
        {
            var boundSet = sets.FirstOrDefault(s =>
                string.Equals(s.Id, binding.Set, StringComparison.OrdinalIgnoreCase));
            if (boundSet is not null)
            {
                var boundMatch = FindImageInSet(boundSet, slot);
                if (boundMatch is not null)
                {
                    return ToSelectedImage(boundMatch, boundSet);
                }
            }
        }

        return PickRankedForSlot(sets, slot, priorityCreators);
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

    private static SelectedImage? PickRankedForSlot(
        IReadOnlyList<MediuxSet> sets,
        ImageSlot slot,
        IReadOnlyList<string> priorityCreators)
    {
        var ranked = SetRanker.RankSets(sets, [slot], priorityCreators);
        foreach (var set in ranked)
        {
            var match = FindImageInSet(set, slot);
            if (match is not null)
            {
                return ToSelectedImage(match, set);
            }
        }

        return null;
    }

    private static MediuxImage? FindImageInSet(MediuxSet set, ImageSlot slot)
        => set.Images.FirstOrDefault(i => i.Slot.Equals(slot));

    private static SelectedImage ToSelectedImage(MediuxImage image, MediuxSet sourceSet)
        => new()
        {
            Image = image,
            SourceSet = sourceSet,
            IsPreferred = true
        };

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

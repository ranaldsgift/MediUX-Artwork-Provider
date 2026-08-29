namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Ranks MediUX sets by priority author then need-coverage tuple.
/// </summary>
public static class SetRanker
{
    /// <summary>
    /// Lexicographic coverage against library needs (higher is better).
    /// Order: titlecards → season posters → poster → backdrop → specials → logo → album art.
    /// </summary>
    public readonly record struct NeedCoverage(
        int Titlecards,
        int SeasonPosters,
        int Poster,
        int Backdrop,
        int SpecialsPoster,
        int Logo,
        int AlbumArt) : IComparable<NeedCoverage>
    {
        /// <summary>Gets whether any need is covered.</summary>
        public bool HasAny
            => Titlecards > 0
               || SeasonPosters > 0
               || Poster > 0
               || Backdrop > 0
               || SpecialsPoster > 0
               || Logo > 0
               || AlbumArt > 0;

        /// <inheritdoc />
        public int CompareTo(NeedCoverage other)
        {
            var c = Titlecards.CompareTo(other.Titlecards);
            if (c != 0)
            {
                return c;
            }

            c = SeasonPosters.CompareTo(other.SeasonPosters);
            if (c != 0)
            {
                return c;
            }

            c = Poster.CompareTo(other.Poster);
            if (c != 0)
            {
                return c;
            }

            c = Backdrop.CompareTo(other.Backdrop);
            if (c != 0)
            {
                return c;
            }

            c = SpecialsPoster.CompareTo(other.SpecialsPoster);
            if (c != 0)
            {
                return c;
            }

            c = Logo.CompareTo(other.Logo);
            if (c != 0)
            {
                return c;
            }

            return AlbumArt.CompareTo(other.AlbumArt);
        }
    }

    /// <summary>
    /// Computes need coverage for a set.
    /// </summary>
    public static NeedCoverage ComputeCoverage(MediuxSet set, IReadOnlyList<ImageSlot> needs)
    {
        var provided = new HashSet<ImageSlot>(set.Images.Select(static i => i.Slot));
        var titlecards = 0;
        var seasonPosters = 0;
        var poster = 0;
        var backdrop = 0;
        var specials = 0;
        var logo = 0;
        var albumArt = 0;

        foreach (var need in needs)
        {
            if (!provided.Contains(need))
            {
                continue;
            }

            switch (SetSelector.GetBindingKind(need))
            {
                case SetBindingKind.Titlecards:
                    titlecards++;
                    break;
                case SetBindingKind.SeasonPosters:
                    seasonPosters++;
                    break;
                case SetBindingKind.Poster:
                    poster++;
                    break;
                case SetBindingKind.Backdrop:
                    backdrop++;
                    break;
                case SetBindingKind.SpecialsPoster:
                    specials++;
                    break;
                case SetBindingKind.Logo:
                    logo++;
                    break;
                case SetBindingKind.AlbumArt:
                    albumArt++;
                    break;
            }
        }

        return new NeedCoverage(titlecards, seasonPosters, poster, backdrop, specials, logo, albumArt);
    }

    /// <summary>
    /// Ranks sets: first priority author with usable art (their sets by coverage), then remaining sets by coverage.
    /// </summary>
    public static IReadOnlyList<MediuxSet> RankSets(
        IReadOnlyList<MediuxSet> sets,
        IReadOnlyList<ImageSlot> needs,
        IReadOnlyList<string> priorityCreators)
    {
        if (sets.Count == 0 || needs.Count == 0)
        {
            return [];
        }

        var scored = sets
            .Select(s => (Set: s, Score: ComputeCoverage(s, needs)))
            .Where(static x => x.Score.HasAny)
            .ToList();

        if (scored.Count == 0)
        {
            return [];
        }

        foreach (var creator in priorityCreators)
        {
            if (string.IsNullOrWhiteSpace(creator))
            {
                continue;
            }

            var creatorSets = scored
                .Where(x => string.Equals(x.Set.Username, creator, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (creatorSets.Count == 0)
            {
                continue;
            }

            var used = new HashSet<string>(
                creatorSets.Select(static x => x.Set.Id),
                StringComparer.OrdinalIgnoreCase);

            return OrderScored(creatorSets)
                .Concat(OrderScored(scored.Where(x => !used.Contains(x.Set.Id))))
                .Select(static x => x.Set)
                .ToList();
        }

        return OrderScored(scored).Select(static x => x.Set).ToList();
    }

    /// <summary>
    /// Returns whether the username appears on the priority creator list.
    /// </summary>
    public static bool IsPriorityAuthor(string? username, IReadOnlyList<string> priorityCreators)
        => IndexOfAuthor(priorityCreators, username ?? string.Empty) >= 0;

    /// <summary>
    /// Assigns a wanted set per binding kind present in needs.
    /// Uses the bound set when present in the catalogue; otherwise the highest-ranked set for the kind.
    /// </summary>
    public static Dictionary<SetBindingKind, MediuxSet> AssignWantedSets(
        IReadOnlyList<MediuxSet> ranked,
        IReadOnlyList<ImageSlot> needs,
        SetBindings? bindings,
        IReadOnlyDictionary<string, MediuxSet> setsById)
    {
        var result = new Dictionary<SetBindingKind, MediuxSet>();
        var kinds = needs
            .Select(SetSelector.GetBindingKind)
            .Where(static k => k is not null)
            .Select(static k => k!.Value)
            .Distinct()
            .ToList();

        foreach (var kind in kinds)
        {
            var kindNeeds = needs.Where(n => SetSelector.GetBindingKind(n) == kind).ToList();
            var existing = bindings?.Get(kind);

            if (existing is not null
                && !string.IsNullOrWhiteSpace(existing.Set)
                && setsById.TryGetValue(existing.Set, out var boundSet))
            {
                result[kind] = boundSet;
                continue;
            }

            foreach (var set in ranked)
            {
                if (SetSelector.Score(set, kindNeeds) > 0)
                {
                    result[kind] = set;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns slot keys for season/titlecard needs not covered by the wanted set.
    /// </summary>
    public static List<string>? ComputeMissing(MediuxSet? set, SetBindingKind kind, IReadOnlyList<ImageSlot> kindNeeds)
    {
        if (kind is not (SetBindingKind.SeasonPosters or SetBindingKind.Titlecards))
        {
            return null;
        }

        var provided = set is null
            ? new HashSet<ImageSlot>()
            : new HashSet<ImageSlot>(set.Images.Select(static i => i.Slot));

        var missing = new List<string>();
        foreach (var need in kindNeeds)
        {
            if (provided.Contains(need))
            {
                continue;
            }

            var key = UpgradeUntil.SlotKey(need);
            if (!string.IsNullOrEmpty(key))
            {
                missing.Add(key);
            }
        }

        return missing.Count == 0 ? null : missing;
    }

    /// <summary>
    /// Returns whether candidate author is strictly higher on the priority list than current.
    /// </summary>
    public static bool IsStrictlyHigherAuthor(
        string? candidate,
        string? current,
        IReadOnlyList<string> priorityCreators)
    {
        if (string.IsNullOrWhiteSpace(candidate) || priorityCreators.Count == 0)
        {
            return false;
        }

        var candidateIndex = IndexOfAuthor(priorityCreators, candidate);
        if (candidateIndex < 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return true;
        }

        var currentIndex = IndexOfAuthor(priorityCreators, current);
        if (currentIndex < 0)
        {
            // Off-list authors are below every listed author.
            return true;
        }

        return candidateIndex < currentIndex;
    }

    private static int IndexOfAuthor(IReadOnlyList<string> priorityCreators, string username)
    {
        for (var i = 0; i < priorityCreators.Count; i++)
        {
            if (string.Equals(priorityCreators[i], username, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static IEnumerable<(MediuxSet Set, NeedCoverage Score)> OrderScored(
        IEnumerable<(MediuxSet Set, NeedCoverage Score)> scored)
        => scored
            .OrderByDescending(static x => x.Score)
            .ThenByDescending(static x => x.Set.Images.Count)
            .ThenByDescending(static x => x.Set.EffectivePopularity)
            .ThenByDescending(static x => x.Set.DateUpdated);
}

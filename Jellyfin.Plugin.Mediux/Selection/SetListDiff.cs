namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Diffs MediUX set-list cache payloads for Browse By refresh.
/// </summary>
public static class SetListDiff
{
    /// <summary>
    /// Per-set asset identity changes between a prior and fresh set list.
    /// </summary>
    public sealed class SetAssetDiff
    {
        /// <summary>Gets or sets asset ids present only in the fresh set.</summary>
        public List<string> Added { get; set; } = [];

        /// <summary>Gets or sets asset ids present in both with a different ModifiedOn/version.</summary>
        public List<string> Changed { get; set; } = [];

        /// <summary>Gets or sets asset ids present only in the prior set.</summary>
        public List<string> Removed { get; set; } = [];
    }

    /// <summary>
    /// Diffs all sets by id using assetId + ModifiedOn.
    /// </summary>
    public static Dictionary<string, SetAssetDiff> Diff(
        IReadOnlyList<MediuxSet>? prior,
        IReadOnlyList<MediuxSet> fresh)
    {
        var result = new Dictionary<string, SetAssetDiff>(StringComparer.OrdinalIgnoreCase);
        var priorById = (prior ?? [])
            .Where(static s => !string.IsNullOrWhiteSpace(s.Id))
            .ToDictionary(static s => s.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var set in fresh)
        {
            if (string.IsNullOrWhiteSpace(set.Id))
            {
                continue;
            }

            priorById.TryGetValue(set.Id, out var oldSet);
            result[set.Id] = DiffOne(oldSet, set);
        }

        foreach (var (id, oldSet) in priorById)
        {
            if (result.ContainsKey(id))
            {
                continue;
            }

            result[id] = new SetAssetDiff
            {
                Removed = oldSet.Images
                    .Where(static i => !string.IsNullOrWhiteSpace(i.AssetId))
                    .Select(static i => i.AssetId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };
        }

        return result;
    }

    /// <summary>
    /// Diffs a single set.
    /// </summary>
    public static SetAssetDiff DiffOne(MediuxSet? prior, MediuxSet fresh)
    {
        var diff = new SetAssetDiff();
        var priorMap = BuildAssetMap(prior);
        var freshMap = BuildAssetMap(fresh);

        foreach (var (assetId, version) in freshMap)
        {
            if (!priorMap.TryGetValue(assetId, out var priorVersion))
            {
                diff.Added.Add(assetId);
            }
            else if (!string.Equals(priorVersion, version, StringComparison.Ordinal))
            {
                diff.Changed.Add(assetId);
            }
        }

        foreach (var assetId in priorMap.Keys)
        {
            if (!freshMap.ContainsKey(assetId))
            {
                diff.Removed.Add(assetId);
            }
        }

        return diff;
    }

    private static Dictionary<string, string> BuildAssetMap(MediuxSet? set)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (set?.Images is null)
        {
            return map;
        }

        foreach (var image in set.Images)
        {
            if (string.IsNullOrWhiteSpace(image.AssetId))
            {
                continue;
            }

            map[image.AssetId] = image.ModifiedOn?.Trim() ?? string.Empty;
        }

        return map;
    }
}

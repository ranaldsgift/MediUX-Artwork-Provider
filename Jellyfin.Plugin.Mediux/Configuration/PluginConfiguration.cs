using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Mediux.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the MediUX API bearer token.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets priority creator usernames, one per line or comma-separated (highest first).
    /// </summary>
    public string PriorityCreators { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets excluded creator usernames, one per line or comma-separated.
    /// </summary>
    public string ExcludedCreators { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether only prioritized authors are used for images.
    /// </summary>
    public bool OnlyPrioritizedAuthors { get; set; }

    /// <summary>
    /// Gets or sets download quality: optimized or original.
    /// </summary>
    public string DownloadQuality { get; set; } = "optimized";

    /// <summary>
    /// Gets or sets a value indicating whether MediUX album art is offered as Jellyfin Box images.
    /// </summary>
    public bool MapAlbumArtToBox { get; set; }

    /// <summary>
    /// Gets or sets the max number of simultaneous image downloads within a single set (1–16).
    /// </summary>
    public int SetDownloadConcurrency { get; set; } = 6;

    /// <summary>
    /// Gets or sets a value indicating whether Upgrade Until tracking is enabled.
    /// </summary>
    public bool EnableUpgradeUntil { get; set; }

    /// <summary>
    /// Gets or sets the exclusive index into <see cref="PriorityCreators"/> for the Upgrade Until ceiling.
    /// Authors at indices [0 .. UpgradeUntilIndex) are considered desired.
    /// </summary>
    public int UpgradeUntilIndex { get; set; } = 1;

    /// <summary>
    /// Gets or sets how many days MediUX fanart set-list responses stay cached (0 = always refetch).
    /// </summary>
    public int SetListCacheDays { get; set; } = 1;

    /// <summary>
    /// Gets or sets whether the Search Images modal restores the last Browse By selection from browser storage.
    /// </summary>
    public bool RememberBrowseBy { get; set; }

    /// <summary>
    /// Parses <see cref="PriorityCreators"/> into an ordered username list.
    /// </summary>
    /// <returns>Ordered creator usernames.</returns>
    public IReadOnlyList<string> GetPriorityCreatorList()
        => ParseCreatorList(PriorityCreators);

    /// <summary>
    /// Parses <see cref="ExcludedCreators"/> into an ordered username list.
    /// </summary>
    /// <returns>Excluded creator usernames.</returns>
    public IReadOnlyList<string> GetExcludedCreatorList()
        => ParseCreatorList(ExcludedCreators);

    private static IReadOnlyList<string> ParseCreatorList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

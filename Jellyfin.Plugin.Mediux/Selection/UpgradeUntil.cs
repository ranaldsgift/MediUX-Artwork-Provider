using Jellyfin.Plugin.Mediux.Configuration;

namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Helpers for the Upgrade Until author ceiling and undesired slot keys.
/// </summary>
public static class UpgradeUntil
{
    /// <summary>
    /// Formats a season key such as S4.
    /// </summary>
    public static string SeasonKey(int seasonNumber)
        => "S" + seasonNumber;

    /// <summary>
    /// Formats an episode key such as S4E5.
    /// </summary>
    public static string EpisodeKey(int seasonNumber, int episodeNumber)
        => "S" + seasonNumber + "E" + episodeNumber;

    /// <summary>
    /// Builds a slot key for season posters / titlecards arrays, or null for series-level kinds.
    /// </summary>
    public static string? SlotKey(ImageSlot slot)
        => slot.Kind switch
        {
            ImageSlotKind.SeasonPrimary when slot.SeasonNumber is int s => SeasonKey(s),
            ImageSlotKind.EpisodeTitleCard when slot.SeasonNumber is int s && slot.EpisodeNumber is int e
                => EpisodeKey(s, e),
            _ => null
        };

    /// <summary>
    /// Clamps UpgradeUntilIndex for a priority list of the given length.
    /// </summary>
    public static int ClampIndex(int index, int priorityCount)
    {
        if (priorityCount <= 0)
        {
            return 0;
        }

        if (index < 1)
        {
            return 1;
        }

        return index > priorityCount ? priorityCount : index;
    }

    /// <summary>
    /// Returns whether the username is above the Upgrade Until ceiling (desired).
    /// </summary>
    public static bool IsDesiredAuthor(string? username, PluginConfiguration config)
    {
        if (config is null || !config.EnableUpgradeUntil)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        var priority = config.GetPriorityCreatorList();
        if (priority.Count == 0)
        {
            return false;
        }

        var ceiling = ClampIndex(config.UpgradeUntilIndex, priority.Count);
        for (var i = 0; i < ceiling; i++)
        {
            if (string.Equals(priority[i], username, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

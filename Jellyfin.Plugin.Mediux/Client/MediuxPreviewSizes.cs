using Jellyfin.Plugin.Mediux.Selection;

namespace Jellyfin.Plugin.Mediux.Client;

/// <summary>
/// Preview dimensions for MediUX set browser thumbnails.
/// </summary>
public static class MediuxPreviewSizes
{
    /// <summary>Max width for poster-style previews (120px card at 2x).</summary>
    public const int PosterMaxWidth = 240;

    /// <summary>Max width for backdrop-style previews (200px card at 2x).</summary>
    public const int BackdropMaxWidth = 400;

    /// <summary>
    /// Gets the preview max width for a slot kind.
    /// </summary>
    public static int GetMaxWidth(ImageSlotKind kind)
        => kind is ImageSlotKind.Backdrop or ImageSlotKind.EpisodeTitleCard
            ? BackdropMaxWidth
            : PosterMaxWidth;

    /// <summary>
    /// Gets the preview max width from a slot kind name.
    /// </summary>
    public static int GetMaxWidth(string? slotKind)
    {
        if (Enum.TryParse<ImageSlotKind>(slotKind, ignoreCase: true, out var kind))
        {
            return GetMaxWidth(kind);
        }

        return PosterMaxWidth;
    }
}

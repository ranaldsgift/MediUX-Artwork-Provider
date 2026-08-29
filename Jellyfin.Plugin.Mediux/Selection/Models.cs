namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Kind of artwork slot needed for an item.
/// </summary>
public enum ImageSlotKind
{
    /// <summary>Primary poster.</summary>
    Primary,

    /// <summary>Backdrop / background.</summary>
    Backdrop,

    /// <summary>Clear logo.</summary>
    Logo,

    /// <summary>Square album art.</summary>
    AlbumArt,

    /// <summary>Season poster.</summary>
    SeasonPrimary,

    /// <summary>Episode title card.</summary>
    EpisodeTitleCard
}

/// <summary>
/// A needed or provided image slot.
/// </summary>
/// <param name="Kind">Slot kind.</param>
/// <param name="SeasonNumber">Season number when applicable.</param>
/// <param name="EpisodeNumber">Episode number when applicable.</param>
public readonly record struct ImageSlot(ImageSlotKind Kind, int? SeasonNumber = null, int? EpisodeNumber = null);

/// <summary>
/// A single MediUX asset mapped to a slot.
/// </summary>
public sealed class MediuxImage
{
    /// <summary>
    /// Gets or sets the asset UUID.
    /// </summary>
    public string AssetId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the slot this asset fills.
    /// </summary>
    public ImageSlot Slot { get; set; }

    /// <summary>
    /// Gets or sets the modified-on timestamp string from MediUX.
    /// </summary>
    public string? ModifiedOn { get; set; }

    /// <summary>
    /// Gets or sets the language display name when present.
    /// </summary>
    public string? Language { get; set; }
}

/// <summary>
/// A MediUX poster set.
/// </summary>
public sealed class MediuxSet
{
    /// <summary>
    /// Gets or sets the set id.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the set title.
    /// </summary>
    public string SetTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the creator username.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets set popularity.
    /// </summary>
    public double Popularity { get; set; }

    /// <summary>
    /// Gets or sets global popularity.
    /// </summary>
    public double PopularityGlobal { get; set; }

    /// <summary>
    /// Gets or sets the last update time.
    /// </summary>
    public DateTimeOffset? DateUpdated { get; set; }

    /// <summary>
    /// Gets or sets images in the set.
    /// </summary>
    public List<MediuxImage> Images { get; set; } = [];

    /// <summary>
    /// Effective popularity for ranking.
    /// </summary>
    public double EffectivePopularity => PopularityGlobal > 0 ? PopularityGlobal : Popularity;
}

/// <summary>
/// An image chosen by selection, with source metadata.
/// </summary>
public sealed class SelectedImage
{
    /// <summary>
    /// Gets or sets the image.
    /// </summary>
    public required MediuxImage Image { get; set; }

    /// <summary>
    /// Gets or sets the source set.
    /// </summary>
    public required MediuxSet SourceSet { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this image is part of the preferred selection.
    /// </summary>
    public bool IsPreferred { get; set; }
}

/// <summary>
/// Result of set selection.
/// </summary>
public sealed class SelectionResult
{
    /// <summary>
    /// Gets preferred images in download priority order.
    /// </summary>
    public IReadOnlyList<SelectedImage> Preferred { get; init; } = [];

    /// <summary>
    /// Gets alternative images from other sets.
    /// </summary>
    public IReadOnlyList<SelectedImage> Alternatives { get; init; } = [];

    /// <summary>
    /// Gets sticky binding updates discovered during selection (wanted set per category).
    /// </summary>
    public IReadOnlyDictionary<SetBindingKind, ImageTypeBinding> BindingUpdates { get; init; }
        = new Dictionary<SetBindingKind, ImageTypeBinding>();
}

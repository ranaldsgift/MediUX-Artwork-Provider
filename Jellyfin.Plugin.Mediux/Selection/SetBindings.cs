namespace Jellyfin.Plugin.Mediux.Selection;

/// <summary>
/// Sticky binding categories for MediUX set ids keyed by provider.
/// </summary>
public enum SetBindingKind
{
    /// <summary>Series/movie primary poster.</summary>
    Poster,

    /// <summary>All non-specials season posters.</summary>
    SeasonPosters,

    /// <summary>Specials season (season 0) poster.</summary>
    SpecialsPoster,

    /// <summary>Backdrop.</summary>
    Backdrop,

    /// <summary>Episode title cards.</summary>
    Titlecards,

    /// <summary>Album art.</summary>
    AlbumArt,

    /// <summary>Logo.</summary>
    Logo
}

/// <summary>
/// Sticky MediUX set ids for a single provider key.
/// </summary>
public sealed class SetBindings
{
    /// <summary>Gets or sets the poster set id.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the season posters set id.</summary>
    public string? SeasonPosters { get; set; }

    /// <summary>Gets or sets the specials poster set id.</summary>
    public string? SpecialsPoster { get; set; }

    /// <summary>Gets or sets the backdrop set id.</summary>
    public string? Backdrop { get; set; }

    /// <summary>Gets or sets the titlecards set id.</summary>
    public string? Titlecards { get; set; }

    /// <summary>Gets or sets the album art set id.</summary>
    public string? AlbumArt { get; set; }

    /// <summary>Gets or sets the logo set id.</summary>
    public string? Logo { get; set; }

    /// <summary>
    /// Gets the set id for a binding kind.
    /// </summary>
    public string? Get(SetBindingKind kind)
        => kind switch
        {
            SetBindingKind.Poster => Poster,
            SetBindingKind.SeasonPosters => SeasonPosters,
            SetBindingKind.SpecialsPoster => SpecialsPoster,
            SetBindingKind.Backdrop => Backdrop,
            SetBindingKind.Titlecards => Titlecards,
            SetBindingKind.AlbumArt => AlbumArt,
            SetBindingKind.Logo => Logo,
            _ => null
        };

    /// <summary>
    /// Sets the set id for a binding kind.
    /// </summary>
    public void Set(SetBindingKind kind, string? setId)
    {
        switch (kind)
        {
            case SetBindingKind.Poster:
                Poster = setId;
                break;
            case SetBindingKind.SeasonPosters:
                SeasonPosters = setId;
                break;
            case SetBindingKind.SpecialsPoster:
                SpecialsPoster = setId;
                break;
            case SetBindingKind.Backdrop:
                Backdrop = setId;
                break;
            case SetBindingKind.Titlecards:
                Titlecards = setId;
                break;
            case SetBindingKind.AlbumArt:
                AlbumArt = setId;
                break;
            case SetBindingKind.Logo:
                Logo = setId;
                break;
        }
    }

    /// <summary>
    /// Merges partial updates into this binding record.
    /// </summary>
    public void ApplyUpdates(IReadOnlyDictionary<SetBindingKind, string> updates)
    {
        foreach (var (kind, setId) in updates)
        {
            if (!string.IsNullOrWhiteSpace(setId))
            {
                Set(kind, setId);
            }
        }
    }
}

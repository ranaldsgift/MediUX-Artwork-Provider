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
/// Per-image-type wanted MediUX set binding.
/// </summary>
public sealed class ImageTypeBinding
{
    /// <summary>Gets or sets the MediUX set id.</summary>
    public string? Set { get; set; }

    /// <summary>Gets or sets the set author username.</summary>
    public string? Author { get; set; }

    /// <summary>Gets or sets whether automatic rebinding/upgrades are blocked.</summary>
    public bool Locked { get; set; }

    /// <summary>
    /// Gets or sets season/episode keys still missing from the wanted set (S2 / S4E5).
    /// </summary>
    public List<string>? Missing { get; set; }

    /// <summary>
    /// Creates a shallow copy.
    /// </summary>
    public ImageTypeBinding Clone()
        => new()
        {
            Set = Set,
            Author = Author,
            Locked = Locked,
            Missing = Missing is null ? null : [.. Missing]
        };
}

/// <summary>
/// Sticky MediUX set bindings for a single provider key.
/// </summary>
public sealed class SetBindings
{
    /// <summary>Gets or sets the poster binding.</summary>
    public ImageTypeBinding? Poster { get; set; }

    /// <summary>Gets or sets the season posters binding.</summary>
    public ImageTypeBinding? SeasonPosters { get; set; }

    /// <summary>Gets or sets the specials poster binding.</summary>
    public ImageTypeBinding? SpecialsPoster { get; set; }

    /// <summary>Gets or sets the backdrop binding.</summary>
    public ImageTypeBinding? Backdrop { get; set; }

    /// <summary>Gets or sets the titlecards binding.</summary>
    public ImageTypeBinding? Titlecards { get; set; }

    /// <summary>Gets or sets the album art binding.</summary>
    public ImageTypeBinding? AlbumArt { get; set; }

    /// <summary>Gets or sets the logo binding.</summary>
    public ImageTypeBinding? Logo { get; set; }

    /// <summary>
    /// Gets the binding for a kind.
    /// </summary>
    public ImageTypeBinding? Get(SetBindingKind kind)
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
    /// Sets the binding for a kind.
    /// </summary>
    public void Set(SetBindingKind kind, ImageTypeBinding? binding)
    {
        switch (kind)
        {
            case SetBindingKind.Poster:
                Poster = binding;
                break;
            case SetBindingKind.SeasonPosters:
                SeasonPosters = binding;
                break;
            case SetBindingKind.SpecialsPoster:
                SpecialsPoster = binding;
                break;
            case SetBindingKind.Backdrop:
                Backdrop = binding;
                break;
            case SetBindingKind.Titlecards:
                Titlecards = binding;
                break;
            case SetBindingKind.AlbumArt:
                AlbumArt = binding;
                break;
            case SetBindingKind.Logo:
                Logo = binding;
                break;
        }
    }

    /// <summary>
    /// Enumerates all kinds that currently have a set id.
    /// </summary>
    public IEnumerable<(SetBindingKind Kind, ImageTypeBinding Binding)> EnumerateBound()
    {
        foreach (SetBindingKind kind in Enum.GetValues<SetBindingKind>())
        {
            var binding = Get(kind);
            if (binding is not null && !string.IsNullOrWhiteSpace(binding.Set))
            {
                yield return (kind, binding);
            }
        }
    }
}

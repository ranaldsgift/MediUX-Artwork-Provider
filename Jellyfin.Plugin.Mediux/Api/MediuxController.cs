using System.Reflection;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Mediux.Client;
using Jellyfin.Plugin.Mediux.Selection;
using Jellyfin.Plugin.Mediux.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Api;

/// <summary>
/// API controller for MediUX set browser.
/// </summary>
[ApiController]
[Route("MediUX")]
[Authorize]
public class MediuxController : ControllerBase
{
    private readonly MediuxApiClient _apiClient;
    private readonly MediuxPreviewService _previewService;
    private readonly MediuxSetBindingStore _bindingStore;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediuxController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediuxController"/> class.
    /// </summary>
    public MediuxController(
        MediuxApiClient apiClient,
        MediuxPreviewService previewService,
        MediuxSetBindingStore bindingStore,
        ILibraryManager libraryManager,
        ILogger<MediuxController> logger)
    {
        _apiClient = apiClient;
        _previewService = previewService;
        _bindingStore = bindingStore;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets available MediUX sets for a Jellyfin item.
    /// </summary>
    [HttpGet("Sets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetSets(
        [FromQuery] Guid itemId,
        [FromQuery] bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        if (!_apiClient.HasApiKey)
        {
            return Ok(Array.Empty<SetBrowserDto>());
        }

        IReadOnlyList<MediuxSet> sets;
        IReadOnlyList<MediuxSet>? prior = null;
        try
        {
            (sets, prior) = await ResolveSetsWithPriorAsync(item, forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Error fetching sets for item {Name}", item.Name);
            return Ok(Array.Empty<SetBrowserDto>());
        }

        var config = Plugin.Instance?.Configuration;
        var priorityCreators = config?.GetPriorityCreatorList() ?? [];
        var excludedCreators = config?.GetExcludedCreatorList() ?? [];
        var onlyPrioritized = config?.OnlyPrioritizedAuthors == true;
        sets = SetSelector.FilterSets(sets, priorityCreators, excludedCreators, onlyPrioritized);
        var orderedSets = SetSelector.OrderSetsForBrowser(sets, priorityCreators);

        var result = orderedSets.Select(MapSetBrowserDto).ToList();

        if (!forceRefresh)
        {
            return Ok(result);
        }

        var priorFiltered = prior is null
            ? null
            : SetSelector.FilterSets(prior, priorityCreators, excludedCreators, onlyPrioritized);
        var diffs = SetListDiff.Diff(priorFiltered, orderedSets)
            .ToDictionary(
                static kv => kv.Key,
                static kv => new
                {
                    added = kv.Value.Added,
                    changed = kv.Value.Changed,
                    removed = kv.Value.Removed
                },
                StringComparer.OrdinalIgnoreCase);

        return Ok(new { sets = result, diffs });
    }

    /// <summary>
    /// Merges sticky MediUX set bindings for a provider key.
    /// </summary>
    [HttpPost("SetBindings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult UpdateSetBindings([FromBody] SetBindingsUpdateDto request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProviderKey))
        {
            return BadRequest();
        }

        var providerKey = request.ProviderKey.Trim();
        var updates = new Dictionary<SetBindingKind, ImageTypeBinding>();
        AddBindingUpdate(updates, SetBindingKind.Poster, request.Poster);
        AddBindingUpdate(updates, SetBindingKind.SeasonPosters, request.SeasonPosters);
        AddBindingUpdate(updates, SetBindingKind.SpecialsPoster, request.SpecialsPoster);
        AddBindingUpdate(updates, SetBindingKind.Backdrop, request.Backdrop);
        AddBindingUpdate(updates, SetBindingKind.Titlecards, request.Titlecards);
        AddBindingUpdate(updates, SetBindingKind.AlbumArt, request.AlbumArt);
        AddBindingUpdate(updates, SetBindingKind.Logo, request.Logo);

        if (updates.Count > 0)
        {
            _bindingStore.MergeManual(providerKey, updates, request.LockSets == true);
        }

        return NoContent();
    }

    /// <summary>
    /// Gets sticky MediUX set bindings for a Jellyfin item.
    /// </summary>
    [HttpGet("SetBindings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetSetBindings([FromQuery] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var keyItem = ResolveBindingKeyItem(item);
        var providerKey = MediuxSetBindingStore.GetProviderKey(keyItem);
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return Ok(new { providerKey = (string?)null });
        }

        var bindings = _bindingStore.Get(providerKey) ?? new SetBindings();
        return Ok(new SetBindingsResponseDto
        {
            ProviderKey = providerKey,
            Poster = ToBindingDto(bindings.Poster),
            SeasonPosters = ToBindingDto(bindings.SeasonPosters),
            SpecialsPoster = ToBindingDto(bindings.SpecialsPoster),
            Backdrop = ToBindingDto(bindings.Backdrop),
            Titlecards = ToBindingDto(bindings.Titlecards),
            AlbumArt = ToBindingDto(bindings.AlbumArt),
            Logo = ToBindingDto(bindings.Logo)
        });
    }

    private static ImageTypeBindingDto? ToBindingDto(ImageTypeBinding? binding)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.Set))
        {
            return null;
        }

        return new ImageTypeBindingDto
        {
            Set = binding.Set,
            Author = binding.Author,
            Locked = binding.Locked,
            Missing = binding.Missing is null ? null : [.. binding.Missing]
        };
    }

    /// <summary>
    /// Resolves the sticky provider key for a Jellyfin item.
    /// </summary>
    [HttpGet("ProviderKey")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> GetProviderKey([FromQuery] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var keyItem = ResolveBindingKeyItem(item);
        var providerKey = MediuxSetBindingStore.GetProviderKey(keyItem);
        return Ok(new { providerKey });
    }

    private static BaseItem ResolveBindingKeyItem(BaseItem item)
        => item switch
        {
            Season season when season.Series is not null => season.Series,
            Episode episode when episode.Series is not null => episode.Series,
            _ => item
        };

    private static void AddBindingUpdate(
        IDictionary<SetBindingKind, ImageTypeBinding> updates,
        SetBindingKind kind,
        ImageTypeBindingDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.Set))
        {
            return;
        }

        updates[kind] = new ImageTypeBinding
        {
            Set = dto.Set.Trim(),
            Author = dto.Author?.Trim(),
            Locked = dto.Locked,
            Missing = dto.Missing is null ? null : [.. dto.Missing]
        };
    }

    private SetBrowserDto MapSetBrowserDto(MediuxSet s)
        => new()
        {
            SetId = s.Id,
            SetTitle = s.SetTitle,
            Username = s.Username,
            Popularity = s.EffectivePopularity,
            ImageCount = s.Images.Count,
            Images = s.Images.Select(img =>
            {
                var previewWidth = MediuxPreviewSizes.GetMaxWidth(img.Slot.Kind);
                return new SetImageDto
                {
                    AssetId = img.AssetId,
                    SlotKind = img.Slot.Kind.ToString(),
                    SeasonNumber = img.Slot.SeasonNumber,
                    EpisodeNumber = img.Slot.EpisodeNumber,
                    Url = _apiClient.BuildAssetUrl(img.AssetId, img.ModifiedOn),
                    PreviewUrl = _apiClient.BuildPreviewUrl(img.AssetId, img.ModifiedOn, previewWidth),
                    Version = MediuxApiClient.FormatAssetVersion(img.ModifiedOn),
                    PreviewWidth = previewWidth,
                    Language = img.Language
                };
            }).ToList()
        };

    private async Task<(IReadOnlyList<MediuxSet> Sets, IReadOnlyList<MediuxSet>? Prior)> ResolveSetsWithPriorAsync(
        BaseItem item,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie => await FetchMovieSetsWithPriorAsync(item, forceRefresh, cancellationToken).ConfigureAwait(false),
            Series series => await FetchShowSetsWithPriorAsync(series, forceRefresh, cancellationToken).ConfigureAwait(false),
            Season season when season.Series is not null
                => await FetchShowSetsWithPriorAsync(season.Series, forceRefresh, cancellationToken).ConfigureAwait(false),
            Episode episode when episode.Series is not null
                => await FetchShowSetsWithPriorAsync(episode.Series, forceRefresh, cancellationToken).ConfigureAwait(false),
            _ => ([], null)
        };
    }

    private async Task<(IReadOnlyList<MediuxSet> Sets, IReadOnlyList<MediuxSet>? Prior)> FetchMovieSetsWithPriorAsync(
        BaseItem item,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var tmdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return ([], null);
        }

        return await _apiClient.GetMovieSetsWithPriorAsync(tmdbId, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<MediuxSet> Sets, IReadOnlyList<MediuxSet>? Prior)> FetchShowSetsWithPriorAsync(
        Series series,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var tmdbId = series.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdbId))
        {
            var tvdbId = series.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb);
            if (!string.IsNullOrEmpty(tvdbId))
            {
                tmdbId = await _apiClient.ResolveShowTmdbIdFromTvdbAsync(tvdbId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrEmpty(tmdbId))
        {
            return ([], null);
        }

        return await _apiClient.GetShowSetsWithPriorAsync(tmdbId, forceRefresh, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediuxSet>> ResolveSetsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var (sets, _) = await ResolveSetsWithPriorAsync(item, forceRefresh: false, cancellationToken).ConfigureAwait(false);
        return sets;
    }

    /// <summary>
    /// Gets a resized preview image for a MediUX asset.
    /// </summary>
    [HttpGet("Preview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetPreview(
        [FromQuery] string assetId,
        [FromQuery] string v,
        [FromQuery] int w,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(v))
        {
            return BadRequest();
        }

        var maxWidth = w > 0 ? w : MediuxPreviewSizes.PosterMaxWidth;
        var path = await _previewService.GetPreviewPathAsync(assetId, v, maxWidth, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>
    /// Serves the set browser JavaScript file.
    /// </summary>
    [HttpGet("SetBrowser.js")]
    [AllowAnonymous]
    public ActionResult GetSetBrowserScript()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = typeof(Plugin).Namespace + ".Web.setbrowser.js";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogWarning("MediUX: Embedded resource {Name} not found", resourceName);
            return NotFound();
        }

        return File(stream, "application/javascript");
    }
}

/// <summary>
/// DTO for a MediUX set in the set browser.
/// </summary>
public class SetBrowserDto
{
    /// <summary>Gets or sets the set ID.</summary>
    [JsonPropertyName("setId")]
    public string SetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the set title.</summary>
    [JsonPropertyName("setTitle")]
    public string SetTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the creator username.</summary>
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    /// <summary>Gets or sets the popularity score.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the total image count.</summary>
    [JsonPropertyName("imageCount")]
    public int ImageCount { get; set; }

    /// <summary>Gets or sets the images in this set.</summary>
    [JsonPropertyName("images")]
    public List<SetImageDto> Images { get; set; } = [];
}

/// <summary>
/// DTO for a single image in the set browser.
/// </summary>
public class SetImageDto
{
    /// <summary>Gets or sets the asset ID.</summary>
    [JsonPropertyName("assetId")]
    public string AssetId { get; set; } = string.Empty;

    /// <summary>Gets or sets the slot kind name.</summary>
    [JsonPropertyName("slotKind")]
    public string SlotKind { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets the image URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the preview thumbnail URL.</summary>
    [JsonPropertyName("previewUrl")]
    public string PreviewUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the asset cache version.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the preview max width in pixels.</summary>
    [JsonPropertyName("previewWidth")]
    public int PreviewWidth { get; set; }

    /// <summary>Gets or sets the language.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

/// <summary>
/// Sticky set-binding response for the Browse By UI.
/// </summary>
public class SetBindingsResponseDto
{
    /// <summary>Gets or sets the provider key (tmdb:… / tvdb:…).</summary>
    [JsonPropertyName("providerKey")]
    public string? ProviderKey { get; set; }

    /// <summary>Gets or sets the poster binding.</summary>
    [JsonPropertyName("poster")]
    public ImageTypeBindingDto? Poster { get; set; }

    /// <summary>Gets or sets the season posters binding.</summary>
    [JsonPropertyName("seasonPosters")]
    public ImageTypeBindingDto? SeasonPosters { get; set; }

    /// <summary>Gets or sets the specials poster binding.</summary>
    [JsonPropertyName("specialsPoster")]
    public ImageTypeBindingDto? SpecialsPoster { get; set; }

    /// <summary>Gets or sets the backdrop binding.</summary>
    [JsonPropertyName("backdrop")]
    public ImageTypeBindingDto? Backdrop { get; set; }

    /// <summary>Gets or sets the titlecards binding.</summary>
    [JsonPropertyName("titlecards")]
    public ImageTypeBindingDto? Titlecards { get; set; }

    /// <summary>Gets or sets the album art binding.</summary>
    [JsonPropertyName("albumArt")]
    public ImageTypeBindingDto? AlbumArt { get; set; }

    /// <summary>Gets or sets the logo binding.</summary>
    [JsonPropertyName("logo")]
    public ImageTypeBindingDto? Logo { get; set; }
}

/// <summary>
/// Partial sticky set-binding update payload.
/// </summary>
public class SetBindingsUpdateDto
{
    /// <summary>Gets or sets the provider key (tmdb:… / tvdb:…).</summary>
    [JsonPropertyName("providerKey")]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Gets or sets whether included kinds should be locked.</summary>
    [JsonPropertyName("lockSets")]
    public bool? LockSets { get; set; }

    /// <summary>Gets or sets the poster binding.</summary>
    [JsonPropertyName("poster")]
    public ImageTypeBindingDto? Poster { get; set; }

    /// <summary>Gets or sets the season posters binding.</summary>
    [JsonPropertyName("seasonPosters")]
    public ImageTypeBindingDto? SeasonPosters { get; set; }

    /// <summary>Gets or sets the specials poster binding.</summary>
    [JsonPropertyName("specialsPoster")]
    public ImageTypeBindingDto? SpecialsPoster { get; set; }

    /// <summary>Gets or sets the backdrop binding.</summary>
    [JsonPropertyName("backdrop")]
    public ImageTypeBindingDto? Backdrop { get; set; }

    /// <summary>Gets or sets the titlecards binding.</summary>
    [JsonPropertyName("titlecards")]
    public ImageTypeBindingDto? Titlecards { get; set; }

    /// <summary>Gets or sets the album art binding.</summary>
    [JsonPropertyName("albumArt")]
    public ImageTypeBindingDto? AlbumArt { get; set; }

    /// <summary>Gets or sets the logo binding.</summary>
    [JsonPropertyName("logo")]
    public ImageTypeBindingDto? Logo { get; set; }
}

/// <summary>
/// DTO for a per-type binding update.
/// </summary>
public class ImageTypeBindingDto
{
    /// <summary>Gets or sets the set id.</summary>
    [JsonPropertyName("set")]
    public string? Set { get; set; }

    /// <summary>Gets or sets the author username.</summary>
    [JsonPropertyName("author")]
    public string? Author { get; set; }

    /// <summary>Gets or sets whether the binding is locked.</summary>
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    /// <summary>Gets or sets missing season/episode keys.</summary>
    [JsonPropertyName("missing")]
    public List<string>? Missing { get; set; }
}

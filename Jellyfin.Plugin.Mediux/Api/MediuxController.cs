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
    public async Task<ActionResult<IReadOnlyList<SetBrowserDto>>> GetSets(
        [FromQuery] Guid itemId,
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
        try
        {
            sets = await ResolveSetsAsync(item, cancellationToken).ConfigureAwait(false);
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

        var result = orderedSets.Select(s => new SetBrowserDto
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
        }).ToList();

        return Ok(result);
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

        var updates = new Dictionary<SetBindingKind, string>();
        AddBinding(updates, SetBindingKind.Poster, request.Poster);
        AddBinding(updates, SetBindingKind.SeasonPosters, request.SeasonPosters);
        AddBinding(updates, SetBindingKind.SpecialsPoster, request.SpecialsPoster);
        AddBinding(updates, SetBindingKind.Backdrop, request.Backdrop);
        AddBinding(updates, SetBindingKind.Titlecards, request.Titlecards);
        AddBinding(updates, SetBindingKind.AlbumArt, request.AlbumArt);
        AddBinding(updates, SetBindingKind.Logo, request.Logo);

        if (updates.Count == 0)
        {
            return NoContent();
        }

        _bindingStore.Merge(request.ProviderKey.Trim(), updates);
        return NoContent();
    }

    /// <summary>
    /// Gets sticky MediUX set bindings for a Jellyfin item (set ids only).
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
            return Ok(new
            {
                providerKey = (string?)null,
                poster = (string?)null,
                seasonPosters = (string?)null,
                specialsPoster = (string?)null,
                backdrop = (string?)null,
                titlecards = (string?)null,
                albumArt = (string?)null,
                logo = (string?)null
            });
        }

        var bindings = _bindingStore.Get(providerKey) ?? new SetBindings();
        return Ok(new
        {
            providerKey,
            poster = bindings.Poster,
            seasonPosters = bindings.SeasonPosters,
            specialsPoster = bindings.SpecialsPoster,
            backdrop = bindings.Backdrop,
            titlecards = bindings.Titlecards,
            albumArt = bindings.AlbumArt,
            logo = bindings.Logo
        });
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

    private static void AddBinding(IDictionary<SetBindingKind, string> updates, SetBindingKind kind, string? setId)
    {
        if (!string.IsNullOrWhiteSpace(setId))
        {
            updates[kind] = setId.Trim();
        }
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

    private async Task<IReadOnlyList<MediuxSet>> ResolveSetsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        return item switch
        {
            Movie => await FetchMovieSetsAsync(item, cancellationToken).ConfigureAwait(false),
            Series series => await FetchShowSetsAsync(series, cancellationToken).ConfigureAwait(false),
            Season season when season.Series is not null => await FetchShowSetsAsync(season.Series, cancellationToken).ConfigureAwait(false),
            Episode episode when episode.Series is not null => await FetchShowSetsAsync(episode.Series, cancellationToken).ConfigureAwait(false),
            _ => []
        };
    }

    private async Task<IReadOnlyList<MediuxSet>> FetchMovieSetsAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return [];
        }

        return await _apiClient.GetMovieSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediuxSet>> FetchShowSetsAsync(Series series, CancellationToken cancellationToken)
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
            return [];
        }

        return await _apiClient.GetShowSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
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
/// Partial sticky set-binding update payload.
/// </summary>
public class SetBindingsUpdateDto
{
    /// <summary>Gets or sets the provider key (tmdb:… / tvdb:…).</summary>
    [JsonPropertyName("providerKey")]
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the poster set id.</summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>Gets or sets the season posters set id.</summary>
    [JsonPropertyName("seasonPosters")]
    public string? SeasonPosters { get; set; }

    /// <summary>Gets or sets the specials poster set id.</summary>
    [JsonPropertyName("specialsPoster")]
    public string? SpecialsPoster { get; set; }

    /// <summary>Gets or sets the backdrop set id.</summary>
    [JsonPropertyName("backdrop")]
    public string? Backdrop { get; set; }

    /// <summary>Gets or sets the titlecards set id.</summary>
    [JsonPropertyName("titlecards")]
    public string? Titlecards { get; set; }

    /// <summary>Gets or sets the album art set id.</summary>
    [JsonPropertyName("albumArt")]
    public string? AlbumArt { get; set; }

    /// <summary>Gets or sets the logo set id.</summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }
}

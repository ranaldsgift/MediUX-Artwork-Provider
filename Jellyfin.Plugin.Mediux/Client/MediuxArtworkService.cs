using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediux.Selection;
using Jellyfin.Plugin.Mediux.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Client;

/// <summary>
/// Coordinates MediUX lookups and set selection for Jellyfin items.
/// </summary>
public class MediuxArtworkService
{
    private readonly MediuxApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly MediuxSetBindingStore _bindingStore;
    private readonly ILogger<MediuxArtworkService> _logger;
    private readonly ConcurrentDictionary<string, DownloadBindingHint> _downloadBindingHints =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MediuxArtworkService"/> class.
    /// </summary>
    public MediuxArtworkService(
        MediuxApiClient apiClient,
        ILibraryManager libraryManager,
        MediuxSetBindingStore bindingStore,
        ILogger<MediuxArtworkService> logger)
    {
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _bindingStore = bindingStore;
        _logger = logger;
    }

    /// <summary>
    /// Gets selected remote images for a movie.
    /// </summary>
    public async Task<IReadOnlyList<RemoteImageInfo>> GetMovieImagesAsync(BaseItem item, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MediUX: GetMovieImagesAsync called for {Name} (Id={Id})", item.Name, item.Id);

        if (!_apiClient.HasApiKey)
        {
            _logger.LogDebug("MediUX: Skipping movie {Name} - no API key", item.Name);
            return [];
        }

        var tmdbId = await ResolveMovieTmdbIdAsync(item, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tmdbId))
        {
            _logger.LogWarning("MediUX: Movie {Name} has no TMDB or TVDB id, cannot look up artwork", item.Name);
            return [];
        }

        _logger.LogDebug("MediUX: Movie {Name} resolved to TMDB {TmdbId}", item.Name, tmdbId);

        var sets = await _apiClient.GetMovieSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        sets = ApplyAuthorFilters(sets);
        _logger.LogDebug("MediUX: Movie {Name} (TMDB {TmdbId}): {SetCount} sets available", item.Name, tmdbId, sets.Count);

        if (sets.Count == 0)
        {
            return [];
        }

        var needs = new List<ImageSlot>
        {
            new(ImageSlotKind.Primary),
            new(ImageSlotKind.Backdrop),
            new(ImageSlotKind.Logo)
        };

        var config = Plugin.Instance!.Configuration;
        if (config.MapAlbumArtToBox)
        {
            needs.Add(new ImageSlot(ImageSlotKind.AlbumArt));
        }

        var selection = SelectWithBindings(item, sets, needs);
        var result = MapToRemoteImages(
            selection,
            includeKinds: null,
            seasonFilter: null,
            episodeFilter: null,
            providerKey: MediuxSetBindingStore.GetProviderKey(item));
        _logger.LogInformation("MediUX: Movie {Name}: returning {Count} remote images", item.Name, result.Count);
        return result;
    }

    /// <summary>
    /// Gets selected remote images for a series (show poster/backdrop).
    /// </summary>
    public async Task<IReadOnlyList<RemoteImageInfo>> GetSeriesImagesAsync(Series series, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MediUX: GetSeriesImagesAsync called for {Name}", series.Name);

        if (!_apiClient.HasApiKey)
        {
            return [];
        }

        var tmdbId = await ResolveShowTmdbIdAsync(series, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tmdbId))
        {
            _logger.LogWarning("MediUX: Series {Name} has no TMDB or TVDB id", series.Name);
            return [];
        }

        _logger.LogDebug("MediUX: Series {Name} resolved to TMDB {TmdbId}", series.Name, tmdbId);

        var sets = await _apiClient.GetShowSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        sets = ApplyAuthorFilters(sets);
        _logger.LogDebug("MediUX: Series {Name}: {SetCount} sets available", series.Name, sets.Count);

        if (sets.Count == 0)
        {
            return [];
        }

        var needs = BuildShowNeeds(series);
        var config = Plugin.Instance!.Configuration;
        var selection = SelectWithBindings(series, sets, needs);

        var includeKinds = new HashSet<ImageSlotKind> { ImageSlotKind.Primary, ImageSlotKind.Backdrop, ImageSlotKind.Logo };
        if (config.MapAlbumArtToBox)
        {
            includeKinds.Add(ImageSlotKind.AlbumArt);
        }

        var result = MapToRemoteImages(
            selection,
            includeKinds: includeKinds,
            seasonFilter: null,
            episodeFilter: null,
            providerKey: MediuxSetBindingStore.GetProviderKey(series));
        _logger.LogInformation("MediUX: Series {Name}: returning {Count} remote images", series.Name, result.Count);
        return result;
    }

    /// <summary>
    /// Gets selected remote images for a season.
    /// </summary>
    public async Task<IReadOnlyList<RemoteImageInfo>> GetSeasonImagesAsync(Season season, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MediUX: GetSeasonImagesAsync called for {Name} S{SeasonNum}", season.SeriesName, season.IndexNumber);

        if (!_apiClient.HasApiKey || season.IndexNumber is null)
        {
            return [];
        }

        var series = season.Series ?? _libraryManager.GetItemById(season.SeriesId) as Series;
        if (series is null)
        {
            _logger.LogWarning("MediUX: Cannot find parent series for season {Name}", season.Name);
            return [];
        }

        var tmdbId = await ResolveShowTmdbIdAsync(series, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return [];
        }

        var sets = await _apiClient.GetShowSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        sets = ApplyAuthorFilters(sets);
        if (sets.Count == 0)
        {
            return [];
        }

        var needs = BuildShowNeeds(series);
        var selection = SelectWithBindings(series, sets, needs);
        var result = MapToRemoteImages(
            selection,
            includeKinds: [ImageSlotKind.SeasonPrimary],
            seasonFilter: season.IndexNumber,
            episodeFilter: null,
            providerKey: MediuxSetBindingStore.GetProviderKey(series));
        _logger.LogDebug("MediUX: Season S{SeasonNum} of {Name}: returning {Count} images", season.IndexNumber, series.Name, result.Count);
        return result;
    }

    /// <summary>
    /// Gets selected remote images for an episode (title cards).
    /// </summary>
    public async Task<IReadOnlyList<RemoteImageInfo>> GetEpisodeImagesAsync(Episode episode, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MediUX: GetEpisodeImagesAsync called for {Name} S{S}E{E}", episode.SeriesName, episode.ParentIndexNumber, episode.IndexNumber);

        if (!_apiClient.HasApiKey || episode.ParentIndexNumber is null || episode.IndexNumber is null)
        {
            return [];
        }

        var series = episode.Series ?? _libraryManager.GetItemById(episode.SeriesId) as Series;
        if (series is null)
        {
            return [];
        }

        var tmdbId = await ResolveShowTmdbIdAsync(series, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(tmdbId))
        {
            return [];
        }

        var sets = await _apiClient.GetShowSetsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        sets = ApplyAuthorFilters(sets);
        if (sets.Count == 0)
        {
            return [];
        }

        var needs = BuildShowNeeds(series);
        var selection = SelectWithBindings(series, sets, needs);
        var result = MapToRemoteImages(
            selection,
            includeKinds: [ImageSlotKind.EpisodeTitleCard],
            seasonFilter: episode.ParentIndexNumber,
            episodeFilter: episode.IndexNumber,
            providerKey: MediuxSetBindingStore.GetProviderKey(series));
        _logger.LogDebug("MediUX: Episode S{S}E{E} of {Name}: returning {Count} images", episode.ParentIndexNumber, episode.IndexNumber, series.Name, result.Count);
        return result;
    }

    /// <summary>
    /// Downloads an image response and persists sticky bindings when this URL was offered via GetImages.
    /// </summary>
    public async Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        ApplyDownloadBindingHint(url);
        return await _apiClient.GetImageResponseAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private SelectionResult SelectWithBindings(BaseItem item, IReadOnlyList<MediuxSet> sets, IReadOnlyList<ImageSlot> needs)
    {
        var config = Plugin.Instance!.Configuration;
        var providerKey = MediuxSetBindingStore.GetProviderKey(item);
        var bindings = providerKey is null ? null : _bindingStore.Get(providerKey);
        var selection = SetSelector.Select(sets, needs, config.GetPriorityCreatorList(), bindings);

        _logger.LogDebug(
            "MediUX: {Name}: {PreferredCount} preferred, {AltCount} alternative images selected",
            item.Name,
            selection.Preferred.Count,
            selection.Alternatives.Count);

        return selection;
    }

    private void ApplyDownloadBindingHint(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !_downloadBindingHints.TryRemove(url, out var hint))
        {
            return;
        }

        _bindingStore.Merge(
            hint.ProviderKey,
            new Dictionary<SetBindingKind, string> { [hint.Kind] = hint.SetId });

        _logger.LogDebug(
            "MediUX: Persisted binding {Kind}={SetId} for {ProviderKey} after image download",
            hint.Kind,
            hint.SetId,
            hint.ProviderKey);
    }

    private static IReadOnlyList<MediuxSet> ApplyAuthorFilters(IReadOnlyList<MediuxSet> sets)
    {
        var config = Plugin.Instance!.Configuration;
        return SetSelector.FilterSets(
            sets,
            config.GetPriorityCreatorList(),
            config.GetExcludedCreatorList(),
            config.OnlyPrioritizedAuthors);
    }

    private async Task<string?> ResolveMovieTmdbIdAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(tmdb))
        {
            return tmdb;
        }

        var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrEmpty(tvdb))
        {
            return await _apiClient.ResolveMovieTmdbIdFromTvdbAsync(tvdb, cancellationToken).ConfigureAwait(false);
        }

        var imdb = item.GetProviderId(MetadataProvider.Imdb);
        _logger.LogDebug("MediUX: Movie {Name} provider IDs - TMDB: {Tmdb}, TVDB: {Tvdb}, IMDB: {Imdb}",
            item.Name, tmdb ?? "none", tvdb ?? "none", imdb ?? "none");

        return null;
    }

    private async Task<string?> ResolveShowTmdbIdAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(tmdb))
        {
            return tmdb;
        }

        var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrEmpty(tvdb))
        {
            return await _apiClient.ResolveShowTmdbIdFromTvdbAsync(tvdb, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("MediUX: Series {Name} provider IDs - TMDB: {Tmdb}, TVDB: {Tvdb}",
            item.Name, tmdb ?? "none", tvdb ?? "none");

        return null;
    }

    private List<ImageSlot> BuildShowNeeds(Series series)
    {
        var needs = new List<ImageSlot>
        {
            new(ImageSlotKind.Primary),
            new(ImageSlotKind.Backdrop),
            new(ImageSlotKind.Logo)
        };

        var config = Plugin.Instance!.Configuration;
        if (config.MapAlbumArtToBox)
        {
            needs.Add(new ImageSlot(ImageSlotKind.AlbumArt));
        }

        try
        {
            var children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [series.Id],
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode]
            });

            foreach (var season in children.OfType<Season>().Where(s => s.IndexNumber.HasValue))
            {
                needs.Add(new ImageSlot(ImageSlotKind.SeasonPrimary, season.IndexNumber));
            }

            foreach (var episode in children.OfType<Episode>()
                         .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue))
            {
                needs.Add(new ImageSlot(ImageSlotKind.EpisodeTitleCard, episode.ParentIndexNumber, episode.IndexNumber));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediUX: Error building show needs for {Name}, using basic poster+backdrop only", series.Name);
        }

        return needs.Distinct().ToList();
    }

    private IReadOnlyList<RemoteImageInfo> MapToRemoteImages(
        SelectionResult selection,
        HashSet<ImageSlotKind>? includeKinds,
        int? seasonFilter,
        int? episodeFilter,
        string? providerKey)
    {
        var list = new List<RemoteImageInfo>();
        AppendImages(list, selection.Preferred, includeKinds, seasonFilter, episodeFilter, providerKey);
        AppendImages(list, selection.Alternatives, includeKinds, seasonFilter, episodeFilter, providerKey);
        return list;
    }

    private void AppendImages(
        List<RemoteImageInfo> list,
        IReadOnlyList<SelectedImage> images,
        HashSet<ImageSlotKind>? includeKinds,
        int? seasonFilter,
        int? episodeFilter,
        string? providerKey)
    {
        foreach (var selected in images)
        {
            var slot = selected.Image.Slot;
            if (includeKinds is not null && !includeKinds.Contains(slot.Kind))
            {
                continue;
            }

            if (seasonFilter is not null && slot.SeasonNumber != seasonFilter)
            {
                continue;
            }

            if (episodeFilter is not null && slot.EpisodeNumber != episodeFilter)
            {
                continue;
            }

            var (type, width, height) = MapType(slot.Kind);

            var url = _apiClient.BuildAssetUrl(selected.Image.AssetId, selected.Image.ModifiedOn);
            var previewWidth = MediuxPreviewSizes.GetMaxWidth(slot.Kind);
            var previewUrl = _apiClient.BuildPreviewUrl(selected.Image.AssetId, selected.Image.ModifiedOn, previewWidth);
            var providerName = string.IsNullOrEmpty(selected.SourceSet.Username)
                ? "MediUX"
                : "MediUX - " + selected.SourceSet.Username;

            RegisterDownloadBindingHint(url, providerKey, selected);

            list.Add(new RemoteImageInfo
            {
                ProviderName = providerName,
                Type = type,
                Width = width,
                Height = height,
                Url = url,
                Language = NormalizeLanguage(selected.Image.Language),
                ThumbnailUrl = previewUrl
            });
        }
    }

    private void RegisterDownloadBindingHint(string url, string? providerKey, SelectedImage selected)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(selected.SourceSet.Id))
        {
            return;
        }

        var kind = SetSelector.GetBindingKind(selected.Image.Slot);
        if (kind is null)
        {
            return;
        }

        _downloadBindingHints[url] = new DownloadBindingHint(providerKey, selected.SourceSet.Id, kind.Value);
    }

    private static (ImageType Type, int Width, int Height) MapType(ImageSlotKind kind)
        => kind switch
        {
            ImageSlotKind.Primary => (ImageType.Primary, 1000, 1500),
            ImageSlotKind.Backdrop => (ImageType.Backdrop, 1920, 1080),
            ImageSlotKind.Logo => (ImageType.Logo, 800, 310),
            ImageSlotKind.AlbumArt => (ImageType.Box, 1000, 1000),
            ImageSlotKind.SeasonPrimary => (ImageType.Primary, 1000, 1500),
            ImageSlotKind.EpisodeTitleCard => (ImageType.Primary, 1920, 1080),
            _ => (ImageType.Primary, 1000, 1500)
        };

    private static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) ||
            string.Equals(language, "00", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return language.Length <= 5 ? language : null;
    }

    private sealed record DownloadBindingHint(string ProviderKey, string SetId, SetBindingKind Kind);
}

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Mediux.Dtos;
using Jellyfin.Plugin.Mediux.Selection;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Client;

/// <summary>
/// Client for the MediUX GraphQL API and asset CDN.
/// </summary>
public class MediuxApiClient
{
    private const string MovieSetsQuery = """
        query getMovieItemSetsByTMDBID($tmdb_id: ID!) {
          movies_by_id(id: $tmdb_id) {
            id
            movie_sets(
              filter: {
                _or: [
                  { movie_poster: { id: { _neq: null } } }
                  { movie_backdrop: { id: { _neq: null } } }
                  { files: { file_type: { _in: ["album", "logo"] } } }
                ]
              }
            ) {
              id
              set_title
              user_created { username }
              date_created
              date_updated
              popularity
              popularity_global
              movie_poster { id modified_on language { display_name } }
              movie_backdrop { id modified_on language { display_name } }
              files(filter: { file_type: { _in: ["album", "logo"] } }) {
                id
                file_type
                modified_on
                language { display_name }
              }
            }
          }
        }
        """;

    private const string ShowSetsQuery = """
        query getShowItemSetsByTMDBID($tmdb_id: ID!) {
          shows_by_id(id: $tmdb_id) {
            id
            show_sets(
              filter: {
                _or: [
                  { show_poster: { id: { _nnull: true } } }
                  { show_backdrop: { id: { _nnull: true } } }
                  { season_posters: { id: { _nnull: true } } }
                  { titlecards: { id: { _nnull: true } } }
                  { files: { file_type: { _in: ["album", "logo"] } } }
                ]
              }
            ) {
              id
              set_title
              user_created { username }
              date_created
              date_updated
              popularity
              popularity_global
              show_poster { id modified_on language { display_name } }
              show_backdrop { id modified_on language { display_name } }
              season_posters(filter: { season: { season_number: { _nnull: true } } }) {
                season { season_number }
                id
                modified_on
                language { display_name }
              }
              titlecards(
                filter: {
                  episode: {
                    episode_number: { _nnull: true }
                    season_id: { season_number: { _nnull: true } }
                  }
                }
              ) {
                id
                modified_on
                language { display_name }
                episode {
                  episode_number
                  season_id { season_number }
                }
              }
              files(filter: { file_type: { _in: ["album", "logo"] } }) {
                id
                file_type
                modified_on
                language { display_name }
              }
            }
          }
        }
        """;

    private const string ShowTvdbQuery = """
        query findShowTMDBIDByTVDBID($tvdb_id: String!) {
          shows(filter: { tvdb_id: { _eq: $tvdb_id } }) { id tvdb_id }
        }
        """;

    private const string MovieTvdbQuery = """
        query findMovieTMDBIDByTVDBID($tvdb_id: String!) {
          movies(filter: { tvdb_id: { _eq: $tvdb_id } }) { id tvdb_id }
        }
        """;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _config;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediuxApiClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediuxApiClient"/> class.
    /// </summary>
    public MediuxApiClient(
        IHttpClientFactory httpClientFactory,
        IServerConfigurationManager config,
        IFileSystem fileSystem,
        ILogger<MediuxApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether an API key is configured.
    /// </summary>
    public bool HasApiKey
    {
        get
        {
            var has = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.ApiKey);
            if (!has)
            {
                _logger.LogDebug("MediUX: No API key configured");
            }

            return has;
        }
    }

    /// <summary>
    /// Resolves a TMDB id from a TVDB id for shows.
    /// </summary>
    public async Task<string?> ResolveShowTmdbIdFromTvdbAsync(string tvdbId, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath("tvdb-show", tvdbId + ".json");
        if (TryReadCache(cachePath, out var cached) && !string.IsNullOrEmpty(cached))
        {
            _logger.LogDebug("MediUX: TVDB show {TvdbId} -> TMDB {TmdbId} (cached)", tvdbId, cached);
            return cached;
        }

        var data = await PostGraphQlAsync<TvdbLookupData>(
            ShowTvdbQuery,
            new { tvdb_id = tvdbId },
            cancellationToken).ConfigureAwait(false);

        var id = data?.Shows?.FirstOrDefault()?.Id;
        _logger.LogDebug("MediUX: TVDB show {TvdbId} -> TMDB {TmdbId} (API)", tvdbId, id ?? "(not found)");
        await WriteCacheAsync(cachePath, id ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// Resolves a TMDB id from a TVDB id for movies.
    /// </summary>
    public async Task<string?> ResolveMovieTmdbIdFromTvdbAsync(string tvdbId, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath("tvdb-movie", tvdbId + ".json");
        if (TryReadCache(cachePath, out var cached) && !string.IsNullOrEmpty(cached))
        {
            _logger.LogDebug("MediUX: TVDB movie {TvdbId} -> TMDB {TmdbId} (cached)", tvdbId, cached);
            return cached;
        }

        var data = await PostGraphQlAsync<MovieTvdbLookupData>(
            MovieTvdbQuery,
            new { tvdb_id = tvdbId },
            cancellationToken).ConfigureAwait(false);

        var id = data?.Movies?.FirstOrDefault()?.Id;
        _logger.LogDebug("MediUX: TVDB movie {TvdbId} -> TMDB {TmdbId} (API)", tvdbId, id ?? "(not found)");
        await WriteCacheAsync(cachePath, id ?? string.Empty, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>
    /// Gets movie sets for a TMDB id.
    /// </summary>
    public Task<IReadOnlyList<MediuxSet>> GetMovieSetsAsync(string tmdbId, CancellationToken cancellationToken)
        => GetMovieSetsAsync(tmdbId, forceRefresh: false, cancellationToken);

    /// <summary>
    /// Gets movie sets for a TMDB id, optionally bypassing the disk cache.
    /// </summary>
    public async Task<IReadOnlyList<MediuxSet>> GetMovieSetsAsync(
        string tmdbId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var result = await GetMovieSetsWithPriorAsync(tmdbId, forceRefresh, cancellationToken).ConfigureAwait(false);
        return result.Sets;
    }

    /// <summary>
    /// Gets movie sets and the prior cache snapshot (when force-refreshing).
    /// </summary>
    public async Task<(IReadOnlyList<MediuxSet> Sets, IReadOnlyList<MediuxSet>? Prior)> GetMovieSetsWithPriorAsync(
        string tmdbId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath("movies", tmdbId + ".json");
        IReadOnlyList<MediuxSet>? prior = null;
        if (TryReadCacheBytes(cachePath, out var priorBytes))
        {
            prior = DeserializeSets(priorBytes);
        }

        if (!forceRefresh && TryGetFreshCacheBytes(cachePath, out var bytes))
        {
            var cached = DeserializeSets(bytes);
            _logger.LogDebug("MediUX: Movie {TmdbId} has {Count} cached sets", tmdbId, cached?.Count ?? 0);
            return (cached ?? [], prior);
        }

        try
        {
            _logger.LogDebug("MediUX: Fetching movie sets for TMDB {TmdbId} (forceRefresh={Force})", tmdbId, forceRefresh);
            var data = await PostGraphQlAsync<MovieSetsData>(
                MovieSetsQuery,
                new { tmdb_id = tmdbId },
                cancellationToken).ConfigureAwait(false);

            var sets = MapMovieSets(data?.MoviesById?.MovieSets);
            _logger.LogInformation("MediUX: Movie {TmdbId} found {Count} sets with {ImageCount} total images",
                tmdbId, sets.Count, sets.Sum(s => s.Images.Count));

            await WriteJsonCacheAsync(cachePath, sets, cancellationToken).ConfigureAwait(false);
            return (sets, prior);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("MediUX: Movie {TmdbId} not found on MediUX (404)", tmdbId);
            await WriteJsonCacheAsync(cachePath, Array.Empty<MediuxSet>(), cancellationToken).ConfigureAwait(false);
            return ([], prior);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Error fetching movie sets for TMDB {TmdbId}", tmdbId);
            return (prior ?? [], prior);
        }
    }

    /// <summary>
    /// Gets show sets for a TMDB id.
    /// </summary>
    public Task<IReadOnlyList<MediuxSet>> GetShowSetsAsync(string tmdbId, CancellationToken cancellationToken)
        => GetShowSetsAsync(tmdbId, forceRefresh: false, cancellationToken);

    /// <summary>
    /// Gets show sets for a TMDB id, optionally bypassing the disk cache.
    /// </summary>
    public async Task<IReadOnlyList<MediuxSet>> GetShowSetsAsync(
        string tmdbId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var result = await GetShowSetsWithPriorAsync(tmdbId, forceRefresh, cancellationToken).ConfigureAwait(false);
        return result.Sets;
    }

    /// <summary>
    /// Gets show sets and the prior cache snapshot (when force-refreshing).
    /// </summary>
    public async Task<(IReadOnlyList<MediuxSet> Sets, IReadOnlyList<MediuxSet>? Prior)> GetShowSetsWithPriorAsync(
        string tmdbId,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath("shows", tmdbId + ".json");
        IReadOnlyList<MediuxSet>? prior = null;
        if (TryReadCacheBytes(cachePath, out var priorBytes))
        {
            prior = DeserializeSets(priorBytes);
        }

        if (!forceRefresh && TryGetFreshCacheBytes(cachePath, out var bytes))
        {
            var cached = DeserializeSets(bytes);
            _logger.LogDebug("MediUX: Show {TmdbId} has {Count} cached sets", tmdbId, cached?.Count ?? 0);
            return (cached ?? [], prior);
        }

        try
        {
            _logger.LogDebug("MediUX: Fetching show sets for TMDB {TmdbId} (forceRefresh={Force})", tmdbId, forceRefresh);
            var data = await PostGraphQlAsync<ShowSetsData>(
                ShowSetsQuery,
                new { tmdb_id = tmdbId },
                cancellationToken).ConfigureAwait(false);

            var sets = MapShowSets(data?.ShowsById?.ShowSets);
            _logger.LogInformation("MediUX: Show {TmdbId} found {Count} sets with {ImageCount} total images",
                tmdbId, sets.Count, sets.Sum(s => s.Images.Count));

            await WriteJsonCacheAsync(cachePath, sets, cancellationToken).ConfigureAwait(false);
            return (sets, prior);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("MediUX: Show {TmdbId} not found on MediUX (404)", tmdbId);
            await WriteJsonCacheAsync(cachePath, Array.Empty<MediuxSet>(), cancellationToken).ConfigureAwait(false);
            return ([], prior);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Error fetching show sets for TMDB {TmdbId}", tmdbId);
            return (prior ?? [], prior);
        }
    }

    /// <summary>
    /// Builds an asset download URL for the configured quality.
    /// </summary>
    public string BuildAssetUrl(string assetId, string? modifiedOn)
        => BuildAssetUrlFromVersion(assetId, FormatAssetVersion(modifiedOn));

    /// <summary>
    /// Builds an asset download URL using a pre-formatted cache version string.
    /// </summary>
    public string BuildAssetUrlFromVersion(string assetId, string version)
    {
        var quality = Plugin.Instance?.Configuration.DownloadQuality ?? "optimized";
        var builder = new StringBuilder();
        builder.Append(Plugin.ApiBaseUrl);
        builder.Append("/assets/");
        builder.Append(assetId);
        builder.Append("?v=");
        builder.Append(version);
        if (string.Equals(quality, "optimized", StringComparison.OrdinalIgnoreCase))
        {
            builder.Append("&key=jpg");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds a CDN URL for preview generation (always requests JPEG for Skia compatibility).
    /// </summary>
    public string BuildPreviewSourceUrl(string assetId, string version)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Plugin.ApiBaseUrl}/assets/{assetId}?v={version}&key=jpg");

    /// <summary>
    /// Builds a relative Jellyfin API URL for a cached preview thumbnail.
    /// </summary>
    public string BuildPreviewUrl(string assetId, string? modifiedOn, int maxWidth)
    {
        var version = FormatAssetVersion(modifiedOn);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"/MediUX/Preview?assetId={Uri.EscapeDataString(assetId)}&v={Uri.EscapeDataString(version)}&w={maxWidth}");
    }

    /// <summary>
    /// Formats a MediUX asset modified timestamp for cache-busting query parameters.
    /// </summary>
    public static string FormatAssetVersion(string? modifiedOn)
    {
        if (DateTimeOffset.TryParse(modifiedOn, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        return DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Downloads an image from MediUX.
    /// </summary>
    public Task<HttpResponseMessage> GetImageResponseAsync(string url, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MediUX: Downloading image from {Url}", url);
        var client = _httpClientFactory.CreateClient(Plugin.HttpClientName);
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private async Task<T?> PostGraphQlAsync<T>(string query, object variables, CancellationToken cancellationToken) where T : class
    {
        if (!HasApiKey)
        {
            return default;
        }

        var client = _httpClientFactory.CreateClient(Plugin.HttpClientName);

        var requestUri = new Uri(Plugin.ApiBaseUrl + "/graphql");
        _logger.LogDebug("MediUX: POST {Uri}", requestUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(new { query, variables }, options: JsonDefaults.Options)
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: HTTP request to {Uri} failed", requestUri);
            throw;
        }

        using (response)
        {
            var statusCode = response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var body = string.Empty;
                try
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // ignore read errors for diagnostic body
                }

                _logger.LogWarning("MediUX: GraphQL returned HTTP {StatusCode}: {Body}", (int)statusCode, body.Length > 500 ? body[..500] : body);
                response.EnsureSuccessStatusCode();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Read the raw response for debugging
            using var memStream = new MemoryStream();
            await stream.CopyToAsync(memStream, cancellationToken).ConfigureAwait(false);
            var rawBytes = memStream.ToArray();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var rawJson = Encoding.UTF8.GetString(rawBytes);
                _logger.LogDebug("MediUX: GraphQL raw response ({Length} bytes): {Json}",
                    rawBytes.Length,
                    rawJson.Length > 2000 ? rawJson[..2000] + "..." : rawJson);
            }

            var payload = JsonSerializer.Deserialize<GraphQlResponse<T>>(rawBytes, JsonDefaults.Options);

            if (payload?.Errors is { Count: > 0 })
            {
                var message = string.Join("; ", payload.Errors.Select(e => e.Message));
                _logger.LogWarning("MediUX: GraphQL errors: {Message}", message);
            }

            if (payload?.Data is null)
            {
                _logger.LogWarning("MediUX: GraphQL response had null data");
            }

            return payload is null ? default : payload.Data;
        }
    }

    private static List<MediuxSet> MapMovieSets(List<MovieSetDto>? dtos)
    {
        if (dtos is null || dtos.Count == 0)
        {
            return [];
        }

        var result = new List<MediuxSet>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrEmpty(dto.Id))
            {
                continue;
            }

            var images = new List<MediuxImage>();
            AddAsset(images, dto.MoviePoster, new ImageSlot(ImageSlotKind.Primary));
            AddAsset(images, dto.MovieBackdrop, new ImageSlot(ImageSlotKind.Backdrop));
            AddAlbumAndLogoFiles(images, dto.Files);

            result.Add(new MediuxSet
            {
                Id = dto.Id,
                SetTitle = dto.SetTitle ?? string.Empty,
                Username = dto.UserCreated?.Username ?? string.Empty,
                Popularity = dto.Popularity ?? 0,
                PopularityGlobal = dto.PopularityGlobal ?? 0,
                DateUpdated = ParseDate(dto.DateUpdated),
                Images = images
            });
        }

        return result;
    }

    private static List<MediuxSet> MapShowSets(List<ShowSetDto>? dtos)
    {
        if (dtos is null || dtos.Count == 0)
        {
            return [];
        }

        var result = new List<MediuxSet>(dtos.Count);
        foreach (var dto in dtos)
        {
            if (string.IsNullOrEmpty(dto.Id))
            {
                continue;
            }

            var images = new List<MediuxImage>();
            AddAsset(images, dto.ShowPoster, new ImageSlot(ImageSlotKind.Primary));
            AddAsset(images, dto.ShowBackdrop, new ImageSlot(ImageSlotKind.Backdrop));

            if (dto.SeasonPosters is not null)
            {
                foreach (var seasonPoster in dto.SeasonPosters)
                {
                    if (seasonPoster.Season?.SeasonNumber is not int seasonNumber || string.IsNullOrEmpty(seasonPoster.Id))
                    {
                        continue;
                    }

                    images.Add(new MediuxImage
                    {
                        AssetId = seasonPoster.Id,
                        Slot = new ImageSlot(ImageSlotKind.SeasonPrimary, seasonNumber),
                        ModifiedOn = seasonPoster.ModifiedOn,
                        Language = seasonPoster.Language?.DisplayName
                    });
                }
            }

            if (dto.Titlecards is not null)
            {
                foreach (var card in dto.Titlecards)
                {
                    var seasonNumber = card.Episode?.SeasonId?.SeasonNumber;
                    var episodeNumber = card.Episode?.EpisodeNumber;
                    if (seasonNumber is null || episodeNumber is null || string.IsNullOrEmpty(card.Id))
                    {
                        continue;
                    }

                    images.Add(new MediuxImage
                    {
                        AssetId = card.Id,
                        Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, seasonNumber, episodeNumber),
                        ModifiedOn = card.ModifiedOn,
                        Language = card.Language?.DisplayName
                    });
                }
            }

            AddAlbumAndLogoFiles(images, dto.Files);

            result.Add(new MediuxSet
            {
                Id = dto.Id,
                SetTitle = dto.SetTitle ?? string.Empty,
                Username = dto.UserCreated?.Username ?? string.Empty,
                Popularity = dto.Popularity ?? 0,
                PopularityGlobal = dto.PopularityGlobal ?? 0,
                DateUpdated = ParseDate(dto.DateUpdated),
                Images = images
            });
        }

        return result;
    }

    private static void AddAlbumAndLogoFiles(List<MediuxImage> images, List<FileAssetDto>? files)
    {
        if (files is null || files.Count == 0)
        {
            return;
        }

        foreach (var file in files)
        {
            if (string.IsNullOrEmpty(file.Id) || string.IsNullOrEmpty(file.FileType))
            {
                continue;
            }

            ImageSlotKind? kind = file.FileType.Equals("logo", StringComparison.OrdinalIgnoreCase)
                ? ImageSlotKind.Logo
                : file.FileType.Equals("album", StringComparison.OrdinalIgnoreCase)
                    ? ImageSlotKind.AlbumArt
                    : null;

            if (kind is null)
            {
                continue;
            }

            images.Add(new MediuxImage
            {
                AssetId = file.Id,
                Slot = new ImageSlot(kind.Value),
                ModifiedOn = file.ModifiedOn,
                Language = file.Language?.DisplayName
            });
        }
    }

    private static void AddAsset(List<MediuxImage> images, List<AssetDto>? assets, ImageSlot slot)
    {
        var asset = assets?.FirstOrDefault();
        if (asset?.Id is null)
        {
            return;
        }

        images.Add(new MediuxImage
        {
            AssetId = asset.Id,
            Slot = slot,
            ModifiedOn = asset.ModifiedOn,
            Language = asset.Language?.DisplayName
        });
    }

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            ? dto
            : null;
    }

    private static string FormatVersion(string? modifiedOn) => FormatAssetVersion(modifiedOn);

    private string GetCachePath(string folder, string fileName)
    {
        var dir = Path.Combine(_config.ApplicationPaths.CachePath, "mediux", folder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    private bool TryGetFreshCacheBytes(string path, out byte[] bytes)
    {
        bytes = [];
        if (!TryReadCacheBytes(path, out bytes))
        {
            return false;
        }

        var info = _fileSystem.GetFileSystemInfo(path);
        var maxDays = GetSetListCacheDays();
        if (maxDays <= 0)
        {
            return false;
        }

        if ((DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(info)).TotalDays > maxDays)
        {
            _logger.LogDebug("MediUX: Cache expired for {Path}", path);
            return false;
        }

        return true;
    }

    private bool TryReadCacheBytes(string path, out byte[] bytes)
    {
        bytes = [];
        var info = _fileSystem.GetFileSystemInfo(path);
        if (!info.Exists)
        {
            return false;
        }

        bytes = File.ReadAllBytes(path);
        return true;
    }

    private bool TryReadCache(string path, out string value)
    {
        value = string.Empty;
        if (!TryReadCacheBytes(path, out var bytes))
        {
            return false;
        }

        var info = _fileSystem.GetFileSystemInfo(path);
        var maxDays = GetSetListCacheDays();
        if (maxDays > 0 && (DateTime.UtcNow - _fileSystem.GetLastWriteTimeUtc(info)).TotalDays > maxDays)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(bytes).Trim().Trim('"');
        return true;
    }

    private static int GetSetListCacheDays()
    {
        var days = Plugin.Instance?.Configuration.SetListCacheDays ?? 1;
        if (days < 0)
        {
            return 0;
        }

        return days > 30 ? 30 : days;
    }

    private static async Task WriteCacheAsync(string path, string value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonCacheAsync(string path, object value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Options, cancellationToken).ConfigureAwait(false);
    }

    private static List<MediuxSet>? DeserializeSets(byte[] bytes)
        => JsonSerializer.Deserialize<List<MediuxSet>>(bytes, JsonDefaults.Options);
}

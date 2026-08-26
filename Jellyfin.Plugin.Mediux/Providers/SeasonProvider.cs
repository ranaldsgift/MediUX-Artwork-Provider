using Jellyfin.Plugin.Mediux.Client;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Providers;

/// <summary>
/// Season image provider for MediUX.
/// </summary>
public class SeasonProvider : IRemoteImageProvider, IHasOrder
{
    private readonly MediuxArtworkService _artworkService;
    private readonly ILogger<SeasonProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeasonProvider"/> class.
    /// </summary>
    public SeasonProvider(MediuxArtworkService artworkService, ILogger<SeasonProvider> logger)
    {
        _artworkService = artworkService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MediUX";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Season;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        => [ImageType.Primary];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item is not Season season)
            {
                return [];
            }

            return await _artworkService.GetSeasonImagesAsync(season, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Unhandled error in SeasonProvider.GetImages for {Name}", item.Name);
            return [];
        }
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _artworkService.GetImageResponseAsync(url, cancellationToken);
}

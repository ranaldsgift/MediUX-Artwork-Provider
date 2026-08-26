using Jellyfin.Plugin.Mediux.Client;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Providers;

/// <summary>
/// Episode title card provider for MediUX.
/// </summary>
public class EpisodeProvider : IRemoteImageProvider, IHasOrder
{
    private readonly MediuxArtworkService _artworkService;
    private readonly ILogger<EpisodeProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpisodeProvider"/> class.
    /// </summary>
    public EpisodeProvider(MediuxArtworkService artworkService, ILogger<EpisodeProvider> logger)
    {
        _artworkService = artworkService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MediUX";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Episode;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        => [ImageType.Primary];

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            if (item is not Episode episode)
            {
                return [];
            }

            return await _artworkService.GetEpisodeImagesAsync(episode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Unhandled error in EpisodeProvider.GetImages for {Name}", item.Name);
            return [];
        }
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _artworkService.GetImageResponseAsync(url, cancellationToken);
}

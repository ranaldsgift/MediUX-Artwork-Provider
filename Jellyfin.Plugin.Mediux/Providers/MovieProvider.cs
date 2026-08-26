using Jellyfin.Plugin.Mediux.Client;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Providers;

/// <summary>
/// Movie image provider for MediUX.
/// </summary>
public class MovieProvider : IRemoteImageProvider, IHasOrder
{
    private readonly MediuxArtworkService _artworkService;
    private readonly ILogger<MovieProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieProvider"/> class.
    /// </summary>
    public MovieProvider(MediuxArtworkService artworkService, ILogger<MovieProvider> logger)
    {
        _artworkService = artworkService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MediUX";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public bool Supports(BaseItem item) => item is Movie;

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        var types = new List<ImageType> { ImageType.Primary, ImageType.Backdrop, ImageType.Logo };
        if (Plugin.Instance?.Configuration.MapAlbumArtToBox == true)
        {
            types.Add(ImageType.Box);
        }

        return types;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            var images = await _artworkService.GetMovieImagesAsync(item, cancellationToken).ConfigureAwait(false);
            return images;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Unhandled error in MovieProvider.GetImages for {Name}", item.Name);
            return [];
        }
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => _artworkService.GetImageResponseAsync(url, cancellationToken);
}

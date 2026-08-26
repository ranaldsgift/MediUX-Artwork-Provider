using System.Net.Http.Headers;
using Jellyfin.Plugin.Mediux.Client;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Services;

/// <summary>
/// Generates and caches resized MediUX preview images.
/// </summary>
public class MediuxPreviewService
{
    private const int PreviewQuality = 80;

    private readonly MediuxApiClient _apiClient;
    private readonly IImageEncoder _imageEncoder;
    private readonly IServerApplicationPaths _appPaths;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<MediuxPreviewService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediuxPreviewService"/> class.
    /// </summary>
    public MediuxPreviewService(
        MediuxApiClient apiClient,
        IImageEncoder imageEncoder,
        IServerApplicationPaths appPaths,
        IFileSystem fileSystem,
        ILogger<MediuxPreviewService> logger)
    {
        _apiClient = apiClient;
        _imageEncoder = imageEncoder;
        _appPaths = appPaths;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private string PreviewCacheDirectory => Path.Combine(_appPaths.CachePath, "mediux-previews");

    /// <summary>
    /// Gets the cached preview file path, generating it on first request.
    /// </summary>
    public async Task<string?> GetPreviewPathAsync(
        string assetId,
        string version,
        int maxWidth,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        maxWidth = Math.Clamp(maxWidth, 1, 2000);

        var cachePath = GetCacheFilePath(assetId, version, maxWidth);
        if (_fileSystem.FileExists(cachePath))
        {
            return cachePath;
        }

        Directory.CreateDirectory(PreviewCacheDirectory);

        var sourceUrl = _apiClient.BuildPreviewSourceUrl(assetId, version);
        string? tempPath = null;

        try
        {
            using var response = await _apiClient.GetImageResponseAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MediUX: Preview source download failed ({Status}) for asset {AssetId}",
                    response.StatusCode,
                    assetId);
                return null;
            }

            var extension = ResolveSourceExtension(response.Content.Headers.ContentType);
            tempPath = Path.Combine(PreviewCacheDirectory, assetId + extension);
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = File.Create(tempPath))
            {
                await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (!_imageEncoder.SupportsImageEncoding)
            {
                File.Copy(tempPath, cachePath, overwrite: true);
                return cachePath;
            }

            var options = new ImageProcessingOptions
            {
                MaxWidth = maxWidth,
                Quality = PreviewQuality,
                RequiresAutoOrientation = true,
                SupportedOutputFormats = _imageEncoder.SupportedOutputFormats
            };

            var dateModified = _fileSystem.GetLastWriteTimeUtc(tempPath);
            var encodedPath = _imageEncoder.EncodeImage(
                tempPath,
                dateModified,
                cachePath,
                autoOrient: true,
                orientation: null,
                PreviewQuality,
                options,
                ImageFormat.Jpg);

            if (_fileSystem.FileExists(cachePath))
            {
                return cachePath;
            }

            if (!string.IsNullOrEmpty(encodedPath)
                && !string.Equals(encodedPath, tempPath, StringComparison.OrdinalIgnoreCase)
                && _fileSystem.FileExists(encodedPath))
            {
                return encodedPath;
            }

            _logger.LogWarning(
                "MediUX: Preview encode did not produce output for asset {AssetId} (encoder returned {Path})",
                assetId,
                encodedPath ?? "(null)");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediUX: Failed to generate preview for asset {AssetId}", assetId);
            return null;
        }
        finally
        {
            if (tempPath is not null && _fileSystem.FileExists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "MediUX: Failed to delete temp preview source {Path}", tempPath);
                }
            }
        }
    }

    private static string ResolveSourceExtension(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType))
        {
            return ".jpg";
        }

        return mediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ when mediaType.EndsWith("jpeg", StringComparison.OrdinalIgnoreCase) => ".jpg",
            _ when mediaType.EndsWith("png", StringComparison.OrdinalIgnoreCase) => ".png",
            _ when mediaType.EndsWith("webp", StringComparison.OrdinalIgnoreCase) => ".webp",
            _ => ".jpg"
        };
    }

    private string GetCacheFilePath(string assetId, string version, int maxWidth)
    {
        var safeAssetId = assetId.Replace("-", "_", StringComparison.Ordinal);
        var safeVersion = version.Replace("-", "_", StringComparison.Ordinal);
        var fileName = safeAssetId + "_" + safeVersion + "_" + maxWidth + ".jpg";
        return Path.Combine(PreviewCacheDirectory, fileName);
    }
}

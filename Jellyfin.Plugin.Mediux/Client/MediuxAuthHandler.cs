using System.Net.Http.Headers;

namespace Jellyfin.Plugin.Mediux.Client;

/// <summary>
/// Adds the configured MediUX bearer token to outgoing requests.
/// </summary>
public class MediuxAuthHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var apiKey = Plugin.Instance?.Configuration.ApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        if (!request.Headers.Accept.Any())
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        }

        return base.SendAsync(request, cancellationToken);
    }
}

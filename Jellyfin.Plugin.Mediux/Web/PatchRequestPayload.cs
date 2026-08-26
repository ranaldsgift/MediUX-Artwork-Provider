using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mediux.Web;

/// <summary>
/// Payload passed by FileTransformation to transformation callbacks.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the file contents to transform.
    /// </summary>
    [JsonPropertyName("contents")]
    public string? Contents { get; set; }
}

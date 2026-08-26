using System.Globalization;
using Jellyfin.Plugin.Mediux.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Mediux;

/// <summary>
/// The MediUX plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Guid _id = new("c8e4f2a1-9b3d-4e6f-a1c2-7d8e9f0a1b2c");

    /// <summary>
    /// MediUX API base URL.
    /// </summary>
    public const string ApiBaseUrl = "https://images.mediux.io";

    /// <summary>
    /// Named HTTP client used for MediUX requests.
    /// </summary>
    public const string HttpClientName = "Mediux";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "MediUX";

    /// <inheritdoc />
    public override string Description =>
        string.Format(
            CultureInfo.InvariantCulture,
            "Download artwork from MediUX for movies and TV shows.");

    /// <inheritdoc />
    public override Guid Id => _id;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "mediux",
                EmbeddedResourcePath = GetType().Namespace + ".Web.mediux.html",
            },
            new PluginPageInfo
            {
                Name = "mediuxjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.mediux.js",
            },
        ];
    }
}

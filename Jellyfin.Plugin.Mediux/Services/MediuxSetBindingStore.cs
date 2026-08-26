using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Mediux.Selection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Services;

/// <summary>
/// Persists MediUX set bindings keyed by provider id (tmdb:/tvdb:).
/// </summary>
public sealed class MediuxSetBindingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<MediuxSetBindingStore> _logger;
    private readonly object _gate = new();
    private ConcurrentDictionary<string, SetBindings>? _cache;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediuxSetBindingStore"/> class.
    /// </summary>
    public MediuxSetBindingStore(IApplicationPaths appPaths, ILogger<MediuxSetBindingStore> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <summary>
    /// Builds a provider key for sticky bindings: tmdb:{id} or tvdb:{id}.
    /// </summary>
    public static string? GetProviderKey(MediaBrowser.Controller.Entities.BaseItem item)
    {
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(tmdb))
        {
            return "tmdb:" + tmdb.Trim();
        }

        var tvdb = item.GetProviderId(MetadataProvider.Tvdb);
        if (!string.IsNullOrWhiteSpace(tvdb))
        {
            return "tvdb:" + tvdb.Trim();
        }

        return null;
    }

    /// <summary>
    /// Gets bindings for a provider key, or null when none exist.
    /// </summary>
    public SetBindings? Get(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return null;
        }

        var map = Load();
        return map.TryGetValue(providerKey, out var bindings) ? Clone(bindings) : null;
    }

    /// <summary>
    /// Merges partial binding updates for a provider key and persists.
    /// </summary>
    public void Merge(string providerKey, IReadOnlyDictionary<SetBindingKind, string> updates)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || updates.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            if (!map.TryGetValue(providerKey, out var bindings))
            {
                bindings = new SetBindings();
                map[providerKey] = bindings;
            }

            bindings.ApplyUpdates(updates);
            SaveUnlocked(map);
            _cache = new ConcurrentDictionary<string, SetBindings>(map, StringComparer.OrdinalIgnoreCase);
        }
    }

    private ConcurrentDictionary<string, SetBindings> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private ConcurrentDictionary<string, SetBindings> LoadUnlocked()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var path = GetStorePath();
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var dto = JsonSerializer.Deserialize<BindingFileDto>(json, JsonOptions);
                var dict = new ConcurrentDictionary<string, SetBindings>(StringComparer.OrdinalIgnoreCase);
                if (dto?.Bindings is not null)
                {
                    foreach (var (key, value) in dto.Bindings)
                    {
                        if (!string.IsNullOrWhiteSpace(key) && value is not null)
                        {
                            dict[key] = value;
                        }
                    }
                }

                _cache = dict;
                return _cache;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediUX: Failed to load set bindings from {Path}", path);
        }

        _cache = new ConcurrentDictionary<string, SetBindings>(StringComparer.OrdinalIgnoreCase);
        return _cache;
    }

    private void SaveUnlocked(IDictionary<string, SetBindings> map)
    {
        var path = GetStorePath();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dto = new BindingFileDto
            {
                Bindings = map.ToDictionary(static kv => kv.Key, static kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediUX: Failed to save set bindings to {Path}", path);
        }
    }

    private string GetStorePath()
        => Path.Combine(_appPaths.PluginConfigurationsPath, "MediUX", "set-bindings.json");

    private static SetBindings Clone(SetBindings source)
        => new()
        {
            Poster = source.Poster,
            SeasonPosters = source.SeasonPosters,
            SpecialsPoster = source.SpecialsPoster,
            Backdrop = source.Backdrop,
            Titlecards = source.Titlecards,
            AlbumArt = source.AlbumArt,
            Logo = source.Logo
        };

    private sealed class BindingFileDto
    {
        public Dictionary<string, SetBindings>? Bindings { get; set; }
    }
}

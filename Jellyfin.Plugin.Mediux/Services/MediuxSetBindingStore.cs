using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Mediux.Configuration;
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
    /// Returns all binding entries (cloned).
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, SetBindings>> GetAllEntries()
    {
        var map = Load();
        return map
            .Select(static kv => new KeyValuePair<string, SetBindings>(kv.Key, Clone(kv.Value)))
            .ToList();
    }

    /// <summary>
    /// Returns bindings that need author upgrade and/or missing fills.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, SetBindings>> GetEntriesNeedingUpgrade(PluginConfiguration config)
    {
        var map = Load();
        return map
            .Where(kv => NeedsUpgradeWork(kv.Value, config))
            .Select(static kv => new KeyValuePair<string, SetBindings>(kv.Key, Clone(kv.Value)))
            .ToList();
    }

    /// <summary>
    /// Merges automatic selection updates, skipping locked and currently-desired authors
    /// (still refreshes missing when the wanted set is unchanged).
    /// </summary>
    /// <returns>True when bindings were changed and persisted.</returns>
    public bool MergeAutomatic(
        string providerKey,
        IReadOnlyDictionary<SetBindingKind, ImageTypeBinding> updates,
        PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || updates.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            var bindings = GetOrCreateUnlocked(map, providerKey);
            var changed = false;
            foreach (var (kind, update) in updates)
            {
                if (update is null || string.IsNullOrWhiteSpace(update.Set))
                {
                    continue;
                }

                var existing = bindings.Get(kind);
                if (existing is not null)
                {
                    var priority = config.GetPriorityCreatorList();
                    var isStrictUpgrade = SetRanker.IsStrictlyHigherAuthor(
                        update.Author,
                        existing.Author,
                        priority);

                    if (existing.Locked
                        || (UpgradeUntil.IsDesiredAuthor(existing.Author, config) && !isStrictUpgrade))
                    {
                        if (string.Equals(existing.Set, update.Set, StringComparison.OrdinalIgnoreCase))
                        {
                            var newMissing = update.Missing is null ? null : update.Missing.ToList();
                            if (!MissingEquals(existing.Missing, newMissing))
                            {
                                existing.Missing = newMissing is null ? null : [.. newMissing];
                                changed = true;
                            }
                        }

                        continue;
                    }
                }

                var replacement = new ImageTypeBinding
                {
                    Set = update.Set,
                    Author = update.Author,
                    Locked = existing?.Locked == true,
                    Missing = update.Missing is null ? null : [.. update.Missing]
                };

                if (!BindingEquals(existing, replacement))
                {
                    bindings.Set(kind, replacement);
                    changed = true;
                }
            }

            if (!changed)
            {
                return false;
            }

            SaveUnlocked(map);
            _cache = new ConcurrentDictionary<string, SetBindings>(map, StringComparer.OrdinalIgnoreCase);
            return true;
        }
    }

    /// <summary>
    /// Applies manual Download Set binding updates (always overwrites included kinds).
    /// </summary>
    public void MergeManual(
        string providerKey,
        IReadOnlyDictionary<SetBindingKind, ImageTypeBinding> updates,
        bool lockSets)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || updates.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            var bindings = GetOrCreateUnlocked(map, providerKey);
            foreach (var (kind, update) in updates)
            {
                if (update is null || string.IsNullOrWhiteSpace(update.Set))
                {
                    continue;
                }

                bindings.Set(kind, new ImageTypeBinding
                {
                    Set = update.Set.Trim(),
                    Author = update.Author?.Trim(),
                    Locked = lockSets,
                    Missing = update.Missing is null ? null : [.. update.Missing]
                });
            }

            SaveUnlocked(map);
            _cache = new ConcurrentDictionary<string, SetBindings>(map, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Replaces a single kind binding (used by the upgrade task).
    /// </summary>
    public void ReplaceKind(string providerKey, SetBindingKind kind, ImageTypeBinding binding)
    {
        if (string.IsNullOrWhiteSpace(providerKey) || binding is null || string.IsNullOrWhiteSpace(binding.Set))
        {
            return;
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            var bindings = GetOrCreateUnlocked(map, providerKey);
            bindings.Set(kind, binding.Clone());
            SaveUnlocked(map);
            _cache = new ConcurrentDictionary<string, SetBindings>(map, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Updates only the missing list for a kind.
    /// </summary>
    public void UpdateMissing(string providerKey, SetBindingKind kind, List<string>? missing)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return;
        }

        lock (_gate)
        {
            var map = LoadUnlocked();
            if (!map.TryGetValue(providerKey, out var bindings))
            {
                return;
            }

            var existing = bindings.Get(kind);
            if (existing is null)
            {
                return;
            }

            existing.Missing = missing is null ? null : [.. missing];
            SaveUnlocked(map);
            _cache = new ConcurrentDictionary<string, SetBindings>(map, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Returns whether bindings need author upgrade and/or missing fills.
    /// </summary>
    public static bool NeedsUpgradeWork(SetBindings bindings, PluginConfiguration config)
    {
        foreach (var (kind, binding) in bindings.EnumerateBound())
        {
            _ = kind;
            if (binding.Missing is { Count: > 0 })
            {
                return true;
            }

            if (!binding.Locked && !UpgradeUntil.IsDesiredAuthor(binding.Author, config))
            {
                return true;
            }
        }

        return false;
    }

    private static SetBindings GetOrCreateUnlocked(ConcurrentDictionary<string, SetBindings> map, string providerKey)
    {
        if (!map.TryGetValue(providerKey, out var bindings))
        {
            bindings = new SetBindings();
            map[providerKey] = bindings;
        }

        return bindings;
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
            _logger.LogWarning(ex, "MediUX: Failed to load set bindings from {Path} (legacy format is ignored)", path);
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

    private static bool BindingEquals(ImageTypeBinding? existing, ImageTypeBinding replacement)
    {
        if (existing is null)
        {
            return false;
        }

        return string.Equals(existing.Set, replacement.Set, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.Author, replacement.Author, StringComparison.OrdinalIgnoreCase)
            && existing.Locked == replacement.Locked
            && MissingEquals(existing.Missing, replacement.Missing);
    }

    private static bool MissingEquals(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left.SequenceEqual(right);
    }

    private static SetBindings Clone(SetBindings source)
        => new()
        {
            Poster = source.Poster?.Clone(),
            SeasonPosters = source.SeasonPosters?.Clone(),
            SpecialsPoster = source.SpecialsPoster?.Clone(),
            Backdrop = source.Backdrop?.Clone(),
            Titlecards = source.Titlecards?.Clone(),
            AlbumArt = source.AlbumArt?.Clone(),
            Logo = source.Logo?.Clone()
        };

    private sealed class BindingFileDto
    {
        public Dictionary<string, SetBindings>? Bindings { get; set; }
    }
}

using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediux.Client;
using Jellyfin.Plugin.Mediux.Selection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Services;

/// <summary>
/// Periodically upgrades below-ceiling bindings and fills missing season/titlecard slots.
/// </summary>
public sealed class UpdateUndesiredImagesTask : IScheduledTask
{
    private readonly MediuxSetBindingStore _bindingStore;
    private readonly MediuxArtworkService _artworkService;
    private readonly MediuxApiClient _apiClient;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<UpdateUndesiredImagesTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateUndesiredImagesTask"/> class.
    /// </summary>
    public UpdateUndesiredImagesTask(
        MediuxSetBindingStore bindingStore,
        MediuxArtworkService artworkService,
        MediuxApiClient apiClient,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        ILogger<UpdateUndesiredImagesTask> logger)
    {
        _bindingStore = bindingStore;
        _artworkService = artworkService;
        _apiClient = apiClient;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MediUX Update Undesired Images";

    /// <inheritdoc />
    public string Key => "MediuxUpdateUndesiredImages";

    /// <inheritdoc />
    public string Description =>
        "Upgrades MediUX artwork to higher priority authors below the Upgrade Until ceiling and fills missing season/titlecard slots.";

    /// <inheritdoc />
    public string Category => "MediUX";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if NET8_0
            Type = TaskTriggerInfo.TriggerInterval,
#else
            Type = TaskTriggerInfoType.IntervalTrigger,
#endif
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            progress.Report(100);
            return;
        }

        if (!_apiClient.HasApiKey)
        {
            _logger.LogWarning("MediUX: Update Undesired Images skipped (no API key)");
            progress.Report(100);
            return;
        }

        var entries = _bindingStore.GetEntriesNeedingUpgrade(config);
        if (entries.Count == 0)
        {
            _logger.LogInformation("MediUX: Update Undesired Images — nothing to upgrade or fill");
            progress.Report(100);
            return;
        }

        var scanned = 0;
        var upgraded = 0;
        var stillWaiting = 0;

        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / entries.Count);

            var (providerKey, bindings) = entries[i];
            scanned++;

            try
            {
                var count = await UpgradeEntryAsync(providerKey, bindings, cancellationToken).ConfigureAwait(false);
                upgraded += count;
                if (count == 0)
                {
                    stillWaiting++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stillWaiting++;
                _logger.LogWarning(ex, "MediUX: Failed upgrading images for {ProviderKey}", providerKey);
            }
        }

        progress.Report(100);
        _logger.LogInformation(
            "MediUX: Update Undesired Images finished — scanned={Scanned}, upgradedSlots={Upgraded}, stillWaiting={Waiting}",
            scanned,
            upgraded,
            stillWaiting);
    }

    private async Task<int> UpgradeEntryAsync(string providerKey, SetBindings bindings, CancellationToken cancellationToken)
    {
        var item = FindLibraryItem(providerKey);
        if (item is null)
        {
            _logger.LogDebug("MediUX: No library item for binding {ProviderKey}", providerKey);
            return 0;
        }

        var config = Plugin.Instance!.Configuration;
        var priority = config.GetPriorityCreatorList();
        IReadOnlyList<MediuxSet> sets;
        IReadOnlyList<ImageSlot> libraryNeeds;

        if (item is Series series)
        {
            var tmdb = ResolveTmdbId(series, providerKey);
            if (string.IsNullOrEmpty(tmdb))
            {
                return 0;
            }

            sets = await _apiClient.GetShowSetsAsync(tmdb, cancellationToken).ConfigureAwait(false);
            sets = _artworkService.FilterSets(sets);
            libraryNeeds = _artworkService.BuildShowNeedsPublic(series);
        }
        else if (item is Movie movie)
        {
            var tmdb = ResolveTmdbId(movie, providerKey);
            if (string.IsNullOrEmpty(tmdb))
            {
                return 0;
            }

            sets = await _apiClient.GetMovieSetsAsync(tmdb, cancellationToken).ConfigureAwait(false);
            sets = _artworkService.FilterSets(sets);
            libraryNeeds = BuildMovieNeeds(config.MapAlbumArtToBox);
        }
        else
        {
            return 0;
        }

        if (sets.Count == 0)
        {
            return 0;
        }

        var setsById = sets.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        var upgraded = 0;

        foreach (var (kind, binding) in bindings.EnumerateBound())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kindNeeds = libraryNeeds.Where(n => SetSelector.GetBindingKind(n) == kind).ToList();
            if (kindNeeds.Count == 0)
            {
                continue;
            }

            var current = binding.Clone();
            MediuxSet? wantedSet = null;
            if (!string.IsNullOrWhiteSpace(current.Set) && setsById.TryGetValue(current.Set, out var existingSet))
            {
                wantedSet = existingSet;
            }

            if (!current.Locked
                && config.EnableUpgradeUntil
                && !UpgradeUntil.IsDesiredAuthor(current.Author, config))
            {
                var eligible = sets
                    .Where(s => SetRanker.IsStrictlyHigherAuthor(s.Username, current.Author, priority))
                    .ToList();

                if (eligible.Count > 0)
                {
                    var ranked = SetRanker.RankSets(eligible, kindNeeds, priority);
                    var best = ranked.FirstOrDefault(s => SetSelector.Score(s, kindNeeds) > 0);
                    if (best is not null
                        && (wantedSet is null
                            || !string.Equals(best.Id, wantedSet.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        wantedSet = best;
                        current.Set = best.Id;
                        current.Author = best.Username;
                        current.Missing = SetRanker.ComputeMissing(best, kind, kindNeeds);
                        _bindingStore.ReplaceKind(providerKey, kind, current);

                        foreach (var need in kindNeeds)
                        {
                            var match = best.Images.FirstOrDefault(i => i.Slot.Equals(need));
                            if (match is null)
                            {
                                continue;
                            }

                            if (await SaveSlotAsync(item, match, best, cancellationToken).ConfigureAwait(false))
                            {
                                upgraded++;
                            }
                        }

                        continue;
                    }
                }
            }

            // Missing fills from the current bound set (including locked).
            if (wantedSet is null || current.Missing is not { Count: > 0 })
            {
                if (wantedSet is not null)
                {
                    var refreshedMissing = SetRanker.ComputeMissing(wantedSet, kind, kindNeeds);
                    if (!SameMissing(current.Missing, refreshedMissing))
                    {
                        _bindingStore.UpdateMissing(providerKey, kind, refreshedMissing);
                    }
                }

                continue;
            }

            var missingKeys = new HashSet<string>(current.Missing, StringComparer.OrdinalIgnoreCase);
            var stillMissing = new List<string>();
            foreach (var need in kindNeeds)
            {
                var key = UpgradeUntil.SlotKey(need);
                if (key is null || !missingKeys.Contains(key))
                {
                    continue;
                }

                var match = wantedSet.Images.FirstOrDefault(i => i.Slot.Equals(need));
                if (match is null)
                {
                    stillMissing.Add(key);
                    continue;
                }

                if (await SaveSlotAsync(item, match, wantedSet, cancellationToken).ConfigureAwait(false))
                {
                    upgraded++;
                }
                else
                {
                    stillMissing.Add(key);
                }
            }

            _bindingStore.UpdateMissing(
                providerKey,
                kind,
                stillMissing.Count == 0 ? null : stillMissing);
        }

        return upgraded;
    }

    private async Task<bool> SaveSlotAsync(
        BaseItem root,
        MediuxImage image,
        MediuxSet sourceSet,
        CancellationToken cancellationToken)
    {
        var target = ResolveTargetItem(root, image.Slot);
        if (target is null)
        {
            return false;
        }

        var imageType = MapImageType(image.Slot.Kind);
        var url = _apiClient.BuildAssetUrl(image.AssetId, image.ModifiedOn);
        await _providerManager.SaveImage(target, url, imageType, null, cancellationToken).ConfigureAwait(false);
        await target.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "MediUX: Applied {Slot} on {Name} from set {SetId} ({Author})",
            UpgradeUntil.SlotKey(image.Slot) ?? image.Slot.Kind.ToString(),
            target.Name,
            sourceSet.Id,
            sourceSet.Username);
        return true;
    }

    private static List<ImageSlot> BuildMovieNeeds(bool mapAlbumArt)
    {
        var needs = new List<ImageSlot>
        {
            new(ImageSlotKind.Primary),
            new(ImageSlotKind.Backdrop),
            new(ImageSlotKind.Logo)
        };
        if (mapAlbumArt)
        {
            needs.Add(new ImageSlot(ImageSlotKind.AlbumArt));
        }

        return needs;
    }

    private static string? ResolveTmdbId(BaseItem item, string providerKey)
    {
        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrEmpty(tmdb))
        {
            return tmdb;
        }

        if (providerKey.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            return providerKey[5..];
        }

        return null;
    }

    private static bool SameMissing(List<string>? a, List<string>? b)
    {
        if (a is null || a.Count == 0)
        {
            return b is null || b.Count == 0;
        }

        if (b is null || b.Count != a.Count)
        {
            return false;
        }

        var set = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        return b.All(set.Contains);
    }

    private BaseItem? ResolveTargetItem(BaseItem root, ImageSlot slot)
    {
        if (slot.Kind is ImageSlotKind.Primary or ImageSlotKind.Backdrop or ImageSlotKind.Logo or ImageSlotKind.AlbumArt)
        {
            return root;
        }

        if (root is not Series series)
        {
            return null;
        }

        var children = _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [series.Id],
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode]
        });

        if (slot.Kind == ImageSlotKind.SeasonPrimary && slot.SeasonNumber is int sn)
        {
            return children.OfType<Season>().FirstOrDefault(s => s.IndexNumber == sn);
        }

        if (slot.Kind == ImageSlotKind.EpisodeTitleCard
            && slot.SeasonNumber is int es && slot.EpisodeNumber is int ee)
        {
            return children.OfType<Episode>()
                .FirstOrDefault(e => e.ParentIndexNumber == es && e.IndexNumber == ee);
        }

        return null;
    }

    private BaseItem? FindLibraryItem(string providerKey)
    {
        if (!TryParseProviderKey(providerKey, out var provider, out var id))
        {
            return null;
        }

        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
            HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [provider] = id
            }
        };

        return _libraryManager.GetItemList(query).FirstOrDefault();
    }

    private static bool TryParseProviderKey(string providerKey, out string provider, out string id)
    {
        provider = string.Empty;
        id = string.Empty;
        var idx = providerKey.IndexOf(':');
        if (idx <= 0 || idx >= providerKey.Length - 1)
        {
            return false;
        }

        var prefix = providerKey[..idx];
        id = providerKey[(idx + 1)..];
        if (string.Equals(prefix, "tmdb", StringComparison.OrdinalIgnoreCase))
        {
            provider = "Tmdb";
            return true;
        }

        if (string.Equals(prefix, "tvdb", StringComparison.OrdinalIgnoreCase))
        {
            provider = "Tvdb";
            return true;
        }

        return false;
    }

    private static ImageType MapImageType(ImageSlotKind kind)
        => kind switch
        {
            ImageSlotKind.Backdrop => ImageType.Backdrop,
            ImageSlotKind.Logo => ImageType.Logo,
            ImageSlotKind.AlbumArt => ImageType.Box,
            _ => ImageType.Primary
        };
}

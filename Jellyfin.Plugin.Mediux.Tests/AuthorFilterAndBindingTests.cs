using System.Text.Json;
using Jellyfin.Plugin.Mediux.Api;
using Jellyfin.Plugin.Mediux.Configuration;
using Jellyfin.Plugin.Mediux.Selection;
using Jellyfin.Plugin.Mediux.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Mediux.Tests;

public class AuthorFilterAndBindingTests
{
    [Fact]
    public void FilterSets_RemovesExcludedAuthors()
    {
        var sets = new[]
        {
            CreateMovieSet("1", "good", primary: true, backdrop: true),
            CreateMovieSet("2", "bad", primary: true, backdrop: true)
        };

        var filtered = SetSelector.FilterSets(sets, ["good"], ["bad"], onlyPrioritizedAuthors: false);

        Assert.Single(filtered);
        Assert.Equal("good", filtered[0].Username);
    }

    [Fact]
    public void FilterSets_ExcludedWinsOverPriority()
    {
        var sets = new[]
        {
            CreateMovieSet("1", "creator", primary: true, backdrop: true)
        };

        var filtered = SetSelector.FilterSets(sets, ["creator"], ["creator"], onlyPrioritizedAuthors: false);

        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterSets_OnlyPrioritized_EmptyList_ReturnsNone()
    {
        var sets = new[]
        {
            CreateMovieSet("1", "a", primary: true, backdrop: false)
        };

        var filtered = SetSelector.FilterSets(sets, [], [], onlyPrioritizedAuthors: true);

        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterSets_OnlyPrioritized_KeepsPriorityAuthors()
    {
        var sets = new[]
        {
            CreateMovieSet("1", "priority", primary: true, backdrop: true),
            CreateMovieSet("2", "other", primary: true, backdrop: true)
        };

        var filtered = SetSelector.FilterSets(sets, ["priority"], [], onlyPrioritizedAuthors: true);

        Assert.Single(filtered);
        Assert.Equal("priority", filtered[0].Username);
    }

    [Fact]
    public void Select_UsesStickyBinding_WhenSetStillExists()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop)
        };

        var sticky = CreateMovieSet("sticky", "a", primary: true, backdrop: true);
        var popular = CreateMovieSet("popular", "b", primary: true, backdrop: true);
        popular.Popularity = 100;
        popular.PopularityGlobal = 100;

        var bindings = new SetBindings
        {
            Poster = new ImageTypeBinding { Set = "sticky", Author = "a", Locked = true },
            Backdrop = new ImageTypeBinding { Set = "sticky", Author = "a", Locked = true }
        };
        var result = SetSelector.Select([sticky, popular], needs, [], bindings);

        Assert.All(result.Preferred, i => Assert.Equal("sticky", i.SourceSet.Id));
        Assert.Equal("sticky", result.BindingUpdates[SetBindingKind.Poster].Set);
        Assert.Equal("sticky", result.BindingUpdates[SetBindingKind.Backdrop].Set);
        Assert.True(result.BindingUpdates[SetBindingKind.Poster].Locked);
    }

    [Fact]
    public void Select_Remaps_WhenStickySetMissing()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop)
        };

        var remaining = CreateMovieSet("new", "a", primary: true, backdrop: true);
        var bindings = new SetBindings
        {
            Poster = new ImageTypeBinding { Set = "gone", Author = "x" },
            Backdrop = new ImageTypeBinding { Set = "gone", Author = "x" }
        };

        var result = SetSelector.Select([remaining], needs, [], bindings);

        Assert.Equal(2, result.Preferred.Count);
        Assert.All(result.Preferred, i => Assert.Equal("new", i.SourceSet.Id));
        Assert.Equal("new", result.BindingUpdates[SetBindingKind.Poster].Set);
        Assert.Equal("new", result.BindingUpdates[SetBindingKind.Backdrop].Set);
    }

    [Fact]
    public void Select_TitlecardsBinding_StaysWantedSet_WhenGapFillFillsMissingEpisode()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1),
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)
        };

        var priority = new MediuxSet
        {
            Id = "5337",
            Username = "priority",
            Popularity = 10,
            PopularityGlobal = 10,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "5337-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var gapFill = new MediuxSet
        {
            Id = "30294",
            Username = "other",
            Popularity = 100,
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "30294-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                },
                new MediuxImage
                {
                    AssetId = "30294-e2",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)
                }
            ]
        };

        var result = SetSelector.Select([priority, gapFill], needs, ["priority"]);

        Assert.Equal(2, result.Preferred.Count);
        Assert.Contains(result.Preferred, i => i.SourceSet.Id == "5337" && i.Image.Slot.EpisodeNumber == 1);
        Assert.Contains(result.Preferred, i => i.SourceSet.Id == "30294" && i.Image.Slot.EpisodeNumber == 2);
        Assert.Equal("5337", result.BindingUpdates[SetBindingKind.Titlecards].Set);
        Assert.Equal("priority", result.BindingUpdates[SetBindingKind.Titlecards].Author);
        Assert.Contains("S1E2", result.BindingUpdates[SetBindingKind.Titlecards].Missing!);
    }

    [Fact]
    public void Select_AssignsDifferentWantedSets_PerType()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop)
        };

        var posterOnly = CreateMovieSet("p", "a", primary: true, backdrop: false);
        posterOnly.PopularityGlobal = 50;
        var backdropOnly = CreateMovieSet("b", "a", primary: false, backdrop: true);
        backdropOnly.PopularityGlobal = 40;

        var result = SetSelector.Select([posterOnly, backdropOnly], needs, ["a"]);

        Assert.Equal("p", result.BindingUpdates[SetBindingKind.Poster].Set);
        Assert.Equal("b", result.BindingUpdates[SetBindingKind.Backdrop].Set);
    }

    [Fact]
    public void GetBindingKind_MapsSeasonZeroToSpecials()
    {
        Assert.Equal(SetBindingKind.SpecialsPoster, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.SeasonPrimary, 0)));
        Assert.Equal(SetBindingKind.SeasonPosters, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.SeasonPrimary, 1)));
        Assert.Equal(SetBindingKind.Titlecards, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)));
        Assert.Equal(SetBindingKind.Logo, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.Logo)));
    }

    [Fact]
    public void UpgradeUntil_IsDesiredAuthor_RespectsCeilingIndex()
    {
        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = true,
            PriorityCreators = "willtong93\npejamas\ncordsplitter",
            UpgradeUntilIndex = 2
        };

        Assert.True(UpgradeUntil.IsDesiredAuthor("willtong93", config));
        Assert.True(UpgradeUntil.IsDesiredAuthor("pejamas", config));
        Assert.False(UpgradeUntil.IsDesiredAuthor("cordsplitter", config));
        Assert.False(UpgradeUntil.IsDesiredAuthor("someoneelse", config));
    }

    [Fact]
    public void UpgradeUntil_IsDesiredAuthor_WhenDisabled_AllowsAll()
    {
        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = false,
            PriorityCreators = "willtong93",
            UpgradeUntilIndex = 1
        };

        Assert.True(UpgradeUntil.IsDesiredAuthor("anyone", config));
    }

    [Fact]
    public void NeedsUpgradeWork_UsesLiveCutoff_NotStoredDesired()
    {
        var bindings = new SetBindings
        {
            Poster = new ImageTypeBinding { Set = "1", Author = "cordsplitter", Locked = false }
        };

        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = true,
            PriorityCreators = "willtong93\npejamas\ncordsplitter",
            UpgradeUntilIndex = 2
        };

        Assert.True(MediuxSetBindingStore.NeedsUpgradeWork(bindings, config));

        config.UpgradeUntilIndex = 3;
        Assert.False(MediuxSetBindingStore.NeedsUpgradeWork(bindings, config));
    }

    [Fact]
    public void NeedsUpgradeWork_LockedWithMissing_StillNeedsWork()
    {
        var bindings = new SetBindings
        {
            Titlecards = new ImageTypeBinding
            {
                Set = "1",
                Author = "willtong93",
                Locked = true,
                Missing = ["S1E2"]
            }
        };

        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = true,
            PriorityCreators = "willtong93",
            UpgradeUntilIndex = 1
        };

        Assert.True(MediuxSetBindingStore.NeedsUpgradeWork(bindings, config));
    }

    [Fact]
    public void SetRanker_IsStrictlyHigherAuthor()
    {
        var priority = new[] { "a", "b", "c" };
        Assert.True(SetRanker.IsStrictlyHigherAuthor("a", "c", priority));
        Assert.False(SetRanker.IsStrictlyHigherAuthor("c", "a", priority));
        Assert.True(SetRanker.IsStrictlyHigherAuthor("a", "offlist", priority));
        Assert.False(SetRanker.IsStrictlyHigherAuthor("offlist", "a", priority));
    }

    [Fact]
    public void SetListDiff_MarksAddedChangedRemoved()
    {
        var prior = new MediuxSet
        {
            Id = "1",
            Images =
            [
                new MediuxImage { AssetId = "a", ModifiedOn = "1", Slot = new ImageSlot(ImageSlotKind.Primary) },
                new MediuxImage { AssetId = "b", ModifiedOn = "1", Slot = new ImageSlot(ImageSlotKind.Backdrop) }
            ]
        };
        var fresh = new MediuxSet
        {
            Id = "1",
            Images =
            [
                new MediuxImage { AssetId = "a", ModifiedOn = "2", Slot = new ImageSlot(ImageSlotKind.Primary) },
                new MediuxImage { AssetId = "c", ModifiedOn = "1", Slot = new ImageSlot(ImageSlotKind.Logo) }
            ]
        };

        var diff = SetListDiff.DiffOne(prior, fresh);
        Assert.Contains("c", diff.Added);
        Assert.Contains("a", diff.Changed);
        Assert.Contains("b", diff.Removed);
    }

    [Fact]
    public void SetBindingsResponseDto_SerializesBindingSetAsLowercase()
    {
        var dto = new SetBindingsResponseDto
        {
            ProviderKey = "tmdb:815",
            Poster = new ImageTypeBindingDto { Set = "9418", Author = "willtong93" },
            Titlecards = new ImageTypeBindingDto { Set = "36625", Author = "willtong93", Locked = true }
        };

        var json = JsonSerializer.Serialize(dto);

        Assert.Contains("\"set\":\"9418\"", json, StringComparison.Ordinal);
        Assert.Contains("\"author\":\"willtong93\"", json, StringComparison.Ordinal);
        Assert.Contains("\"poster\"", json, StringComparison.Ordinal);
        Assert.Contains("\"locked\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MergeAutomatic_ReturnsFalse_WhenDesiredBindingUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MediuxSetBindingStore(new TestApplicationPaths(root), NullLogger<MediuxSetBindingStore>.Instance);
            var config = new PluginConfiguration
            {
                EnableUpgradeUntil = true,
                PriorityCreators = "willtong93",
                UpgradeUntilIndex = 1
            };

            var updates = new Dictionary<SetBindingKind, ImageTypeBinding>
            {
                [SetBindingKind.Poster] = new ImageTypeBinding { Set = "9418", Author = "willtong93" }
            };

            Assert.True(store.MergeAutomatic("tmdb:815", updates, config));
            Assert.False(store.MergeAutomatic("tmdb:815", updates, config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MergeAutomatic_ReturnsTrue_WhenSetIdChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MediuxSetBindingStore(new TestApplicationPaths(root), NullLogger<MediuxSetBindingStore>.Instance);
            var config = new PluginConfiguration
            {
                EnableUpgradeUntil = true,
                PriorityCreators = "willtong93",
                UpgradeUntilIndex = 1
            };

            store.MergeAutomatic(
                "tmdb:815",
                new Dictionary<SetBindingKind, ImageTypeBinding>
                {
                    [SetBindingKind.Poster] = new ImageTypeBinding { Set = "9418", Author = "nobody" }
                },
                config);

            Assert.True(store.MergeAutomatic(
                "tmdb:815",
                new Dictionary<SetBindingKind, ImageTypeBinding>
                {
                    [SetBindingKind.Poster] = new ImageTypeBinding { Set = "9999", Author = "willtong93" }
                },
                config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpgradeUntil_SlotKey_FormatsSeasonAndEpisode()
    {
        Assert.Equal("S4", UpgradeUntil.SlotKey(new ImageSlot(ImageSlotKind.SeasonPrimary, 4)));
        Assert.Equal("S4E5", UpgradeUntil.SlotKey(new ImageSlot(ImageSlotKind.EpisodeTitleCard, 4, 5)));
        Assert.Null(UpgradeUntil.SlotKey(new ImageSlot(ImageSlotKind.Primary)));
    }

    [Fact]
    public void Select_KeepsUnlockedBinding_WhenSetExists()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
        };

        var bound = new MediuxSet
        {
            Id = "33272",
            Username = "defluo",
            Popularity = 1,
            PopularityGlobal = 1,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "33272-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var popular = new MediuxSet
        {
            Id = "9999",
            Username = "jrkxxx",
            Popularity = 100,
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "9999-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var bindings = new SetBindings
        {
            Titlecards = new ImageTypeBinding { Set = "33272", Author = "defluo", Locked = false }
        };

        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = false,
            PriorityCreators = "jrkxxx"
        };

        var result = SetSelector.Select([bound, popular], needs, config.GetPriorityCreatorList(), bindings, config);

        Assert.Single(result.Preferred);
        Assert.Equal("33272", result.Preferred[0].SourceSet.Id);
        Assert.Equal("33272", result.BindingUpdates[SetBindingKind.Titlecards].Set);
        Assert.Equal("defluo", result.BindingUpdates[SetBindingKind.Titlecards].Author);
    }

    [Fact]
    public void Select_DoesNotUpgradeOffList_WithoutUpgradeUntil()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
        };

        var bound = new MediuxSet
        {
            Id = "33272",
            Username = "defluo",
            Popularity = 1,
            PopularityGlobal = 1,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "33272-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var priority = new MediuxSet
        {
            Id = "9999",
            Username = "jrkxxx",
            Popularity = 100,
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "9999-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var bindings = new SetBindings
        {
            Titlecards = new ImageTypeBinding { Set = "33272", Author = "defluo", Locked = false }
        };

        var config = new PluginConfiguration
        {
            EnableUpgradeUntil = false,
            PriorityCreators = "jrkxxx"
        };

        var result = SetSelector.Select([bound, priority], needs, config.GetPriorityCreatorList(), bindings, config);

        Assert.Single(result.Preferred);
        Assert.Equal("33272", result.Preferred[0].SourceSet.Id);
        Assert.Equal("33272", result.BindingUpdates[SetBindingKind.Titlecards].Set);
    }

    [Fact]
    public void PickImageForSlot_UsesBoundSet_WhenSlotPresent()
    {
        var slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1);
        var bound = new MediuxSet
        {
            Id = "33272",
            Username = "defluo",
            Images =
            [
                new MediuxImage
                {
                    AssetId = "33272-e1",
                    Slot = slot
                }
            ]
        };

        var popular = new MediuxSet
        {
            Id = "9999",
            Username = "jrkxxx",
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "9999-e1",
                    Slot = slot
                }
            ]
        };

        var binding = new ImageTypeBinding { Set = "33272", Author = "defluo" };
        var picked = SetSelector.PickImageForSlot([bound, popular], slot, binding, ["jrkxxx"]);

        Assert.NotNull(picked);
        Assert.Equal("33272", picked!.SourceSet.Id);
        Assert.Equal("33272-e1", picked.Image.AssetId);
    }

    [Fact]
    public void PickImageForSlot_FallsBackToRanked_WhenBoundSetMissingSlot()
    {
        var slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2);
        var bound = new MediuxSet
        {
            Id = "33272",
            Username = "defluo",
            Images =
            [
                new MediuxImage
                {
                    AssetId = "33272-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var fallback = new MediuxSet
        {
            Id = "30294",
            Username = "other",
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "30294-e2",
                    Slot = slot
                }
            ]
        };

        var binding = new ImageTypeBinding { Set = "33272", Author = "defluo" };
        var picked = SetSelector.PickImageForSlot([bound, fallback], slot, binding, []);

        Assert.NotNull(picked);
        Assert.Equal("30294", picked!.SourceSet.Id);
        Assert.Equal("30294-e2", picked.Image.AssetId);
    }

    [Fact]
    public void PickImageForSlot_FallsBackToRanked_WhenBoundSetNotInCatalogue()
    {
        var slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1);
        var ranked = new MediuxSet
        {
            Id = "9999",
            Username = "jrkxxx",
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "9999-e1",
                    Slot = slot
                }
            ]
        };

        var binding = new ImageTypeBinding { Set = "gone", Author = "missing" };
        var picked = SetSelector.PickImageForSlot([ranked], slot, binding, ["jrkxxx"]);

        Assert.NotNull(picked);
        Assert.Equal("9999", picked!.SourceSet.Id);
    }

    [Fact]
    public void Select_GapFill_DoesNotChangeWantedBinding()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1),
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)
        };

        var bound = new MediuxSet
        {
            Id = "33272",
            Username = "defluo",
            Popularity = 1,
            PopularityGlobal = 1,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "33272-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                }
            ]
        };

        var gapFill = new MediuxSet
        {
            Id = "30294",
            Username = "other",
            Popularity = 100,
            PopularityGlobal = 100,
            Images =
            [
                new MediuxImage
                {
                    AssetId = "30294-e1",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
                },
                new MediuxImage
                {
                    AssetId = "30294-e2",
                    Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)
                }
            ]
        };

        var bindings = new SetBindings
        {
            Titlecards = new ImageTypeBinding { Set = "33272", Author = "defluo", Locked = false }
        };

        var result = SetSelector.Select([bound, gapFill], needs, [], bindings);

        Assert.Equal(2, result.Preferred.Count);
        Assert.Contains(result.Preferred, i => i.SourceSet.Id == "33272" && i.Image.Slot.EpisodeNumber == 1);
        Assert.Contains(result.Preferred, i => i.SourceSet.Id == "30294" && i.Image.Slot.EpisodeNumber == 2);
        Assert.Equal("33272", result.BindingUpdates[SetBindingKind.Titlecards].Set);
        Assert.Equal("defluo", result.BindingUpdates[SetBindingKind.Titlecards].Author);
        Assert.Contains("S1E2", result.BindingUpdates[SetBindingKind.Titlecards].Missing!);
    }

    [Fact]
    public void MergeAutomatic_AllowsStrictlyHigherUpgrade_WhenBothDesired()
    {
        var root = Path.Combine(Path.GetTempPath(), "mediux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new MediuxSetBindingStore(new TestApplicationPaths(root), NullLogger<MediuxSetBindingStore>.Instance);
            var config = new PluginConfiguration
            {
                EnableUpgradeUntil = true,
                PriorityCreators = "willtong93\npejamas",
                UpgradeUntilIndex = 2
            };

            store.MergeAutomatic(
                "tmdb:815",
                new Dictionary<SetBindingKind, ImageTypeBinding>
                {
                    [SetBindingKind.Poster] = new ImageTypeBinding { Set = "pejamas", Author = "pejamas" }
                },
                config);

            Assert.True(store.MergeAutomatic(
                "tmdb:815",
                new Dictionary<SetBindingKind, ImageTypeBinding>
                {
                    [SetBindingKind.Poster] = new ImageTypeBinding { Set = "willtong93", Author = "willtong93" }
                },
                config));

            var bindings = store.Get("tmdb:815");
            Assert.NotNull(bindings);
            Assert.Equal("willtong93", bindings!.Poster!.Set);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestApplicationPaths : IApplicationPaths
    {
        public TestApplicationPaths(string root) => PluginConfigurationsPath = root;

        public string PluginConfigurationsPath { get; }

        public string ProgramDataPath => PluginConfigurationsPath;

        public string DataPath => PluginConfigurationsPath;

        public string SystemConfigurationFilePath => Path.Combine(PluginConfigurationsPath, "system.xml");

        public string LogDirectoryPath => PluginConfigurationsPath;

        public string PluginsPath => PluginConfigurationsPath;

        public string TempDirectory => PluginConfigurationsPath;

        public string WebPath => PluginConfigurationsPath;

        public string BackupPath => PluginConfigurationsPath;

        public string ProgramDataDirectory => PluginConfigurationsPath;

        public string ImageCachePath => PluginConfigurationsPath;

        public string CachePath => PluginConfigurationsPath;

        public string MetadataPath => PluginConfigurationsPath;

        public string TraysPath => PluginConfigurationsPath;

        public string ShortcutCachePath => PluginConfigurationsPath;

        public string TranscodingTempPath => PluginConfigurationsPath;

        public string ProgramSystemPath => PluginConfigurationsPath;

        public string ConfigurationDirectoryPath => PluginConfigurationsPath;

        public string VirtualDataPath => PluginConfigurationsPath;

        public string TrickplayPath => PluginConfigurationsPath;

        public Dictionary<string, string>? VirtualEnvironmentPaths { get; set; }

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string marker, bool requireWrite)
        {
        }
    }

    private static MediuxSet CreateMovieSet(string id, string user, bool primary, bool backdrop)
    {
        var images = new List<MediuxImage>();
        if (primary)
        {
            images.Add(new MediuxImage { AssetId = id + "-p", Slot = new ImageSlot(ImageSlotKind.Primary) });
        }

        if (backdrop)
        {
            images.Add(new MediuxImage { AssetId = id + "-b", Slot = new ImageSlot(ImageSlotKind.Backdrop) });
        }

        return new MediuxSet
        {
            Id = id,
            Username = user,
            Popularity = 1,
            PopularityGlobal = 1,
            Images = images
        };
    }
}

using Jellyfin.Plugin.Mediux.Selection;

namespace Jellyfin.Plugin.Mediux.Tests;

public class SetSelectorTests
{
    [Fact]
    public void Prefers_Highest_Priority_Creator_Most_Complete_Set()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop)
        };

        var sets = new[]
        {
            CreateMovieSet("1", "willtong93", popularity: 10, primary: true, backdrop: false),
            CreateMovieSet("2", "willtong93", popularity: 1, primary: true, backdrop: true),
            CreateMovieSet("3", "Pejamas", popularity: 100, primary: true, backdrop: true)
        };

        var result = SetSelector.Select(sets, needs, ["willtong93", "Pejamas"]);

        Assert.Equal(2, result.Preferred.Count);
        Assert.All(result.Preferred, i => Assert.Equal("willtong93", i.SourceSet.Username));
        Assert.Equal("2", result.Preferred[0].SourceSet.Id);
    }

    [Fact]
    public void Uses_Most_Complete_When_No_Priority_Creators()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop),
            new ImageSlot(ImageSlotKind.SeasonPrimary, 1),
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)
        };

        var sets = new[]
        {
            CreateShowSet("small", "a", 50, hasPoster: true, hasBackdrop: false, seasons: [], episodes: []),
            CreateShowSet("big", "b", 5, hasPoster: true, hasBackdrop: true, seasons: [1], episodes: [(1, 1)])
        };

        var result = SetSelector.Select(sets, needs, []);

        Assert.All(result.Preferred, i => Assert.Equal("big", i.SourceSet.Id));
        Assert.Equal(4, result.Preferred.Count);
    }

    [Fact]
    public void Gap_Fills_Remaining_Slots_From_Most_Complete_Other_Sets()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop),
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1),
            new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)
        };

        var sets = new[]
        {
            CreateShowSet("priority", "willtong93", 1, hasPoster: true, hasBackdrop: true, seasons: [], episodes: []),
            CreateShowSet("cards-a", "other1", 10, hasPoster: false, hasBackdrop: false, seasons: [], episodes: [(1, 1)]),
            CreateShowSet("cards-b", "other2", 20, hasPoster: false, hasBackdrop: false, seasons: [], episodes: [(1, 1), (1, 2)])
        };

        var result = SetSelector.Select(sets, needs, ["willtong93"]);

        Assert.Contains(result.Preferred, i => i.Image.Slot.Kind == ImageSlotKind.Primary && i.SourceSet.Id == "priority");
        Assert.Contains(result.Preferred, i => i.Image.Slot.Kind == ImageSlotKind.Backdrop && i.SourceSet.Id == "priority");
        Assert.Contains(result.Preferred, i => i.Image.Slot.Equals(new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 1)) && i.SourceSet.Id == "cards-b");
        Assert.Contains(result.Preferred, i => i.Image.Slot.Equals(new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)) && i.SourceSet.Id == "cards-b");
    }

    [Fact]
    public void Tie_Breaks_Gap_Fill_By_Popularity()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Backdrop)
        };

        var sets = new[]
        {
            CreateMovieSet("poster-only", "creator", 1, primary: true, backdrop: false),
            CreateMovieSet("backdrop-low", "a", 5, primary: false, backdrop: true),
            CreateMovieSet("backdrop-high", "b", 50, primary: false, backdrop: true)
        };

        var result = SetSelector.Select(sets, needs, ["creator"]);

        var backdrop = Assert.Single(result.Preferred, i => i.Image.Slot.Kind == ImageSlotKind.Backdrop);
        Assert.Equal("backdrop-high", backdrop.SourceSet.Id);
    }

    [Fact]
    public void OrderSetsForBrowser_GroupsByPriorityCreatorsThenCompleteness()
    {
        var sets = new[]
        {
            CreateMovieSet("p2-small", "Pejamas", 100, primary: true, backdrop: false),
            CreateMovieSet("p2-big", "Pejamas", 10, primary: true, backdrop: true),
            CreateMovieSet("p1-small", "willtong93", 50, primary: true, backdrop: false),
            CreateMovieSet("p1-big", "willtong93", 5, primary: true, backdrop: true),
            CreateMovieSet("other-big", "random", 200, primary: true, backdrop: true),
            CreateMovieSet("other-small", "random", 1, primary: true, backdrop: false)
        };

        var ordered = SetSelector.OrderSetsForBrowser(sets, ["willtong93", "Pejamas"]);

        Assert.Equal(
            ["p1-big", "p1-small", "p2-big", "p2-small", "other-big", "other-small"],
            ordered.Select(s => s.Id).ToArray());
    }

    [Fact]
    public void Selects_Logo_And_AlbumArt_When_Needed()
    {
        var needs = new[]
        {
            new ImageSlot(ImageSlotKind.Primary),
            new ImageSlot(ImageSlotKind.Logo),
            new ImageSlot(ImageSlotKind.AlbumArt)
        };

        var incomplete = CreateMovieSet("incomplete", "a", 100, primary: true, backdrop: false);
        var complete = CreateMovieSet("complete", "b", 1, primary: true, backdrop: false);
        complete.Images.Add(new MediuxImage { AssetId = "complete-logo", Slot = new ImageSlot(ImageSlotKind.Logo) });
        complete.Images.Add(new MediuxImage { AssetId = "complete-album", Slot = new ImageSlot(ImageSlotKind.AlbumArt) });

        var result = SetSelector.Select([incomplete, complete], needs, []);

        Assert.Equal(3, result.Preferred.Count);
        Assert.Contains(result.Preferred, i => i.Image.Slot.Kind == ImageSlotKind.Logo && i.SourceSet.Id == "complete");
        Assert.Contains(result.Preferred, i => i.Image.Slot.Kind == ImageSlotKind.AlbumArt && i.SourceSet.Id == "complete");
        Assert.All(result.Preferred, i => Assert.Equal("complete", i.SourceSet.Id));
    }

    [Fact]
    public void OrderSetsForBrowser_OrdersUnmatchedByImageCount()
    {
        var sets = new[]
        {
            CreateMovieSet("small", "a", 100, primary: true, backdrop: false),
            CreateMovieSet("big", "b", 1, primary: true, backdrop: true)
        };

        var ordered = SetSelector.OrderSetsForBrowser(sets, []);

        Assert.Equal(["big", "small"], ordered.Select(s => s.Id).ToArray());
    }

    private static MediuxSet CreateMovieSet(string id, string user, double popularity, bool primary, bool backdrop)
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
            Popularity = popularity,
            PopularityGlobal = popularity,
            Images = images
        };
    }

    private static MediuxSet CreateShowSet(
        string id,
        string user,
        double popularity,
        bool hasPoster,
        bool hasBackdrop,
        int[] seasons,
        (int Season, int Episode)[] episodes)
    {
        var images = new List<MediuxImage>();
        if (hasPoster)
        {
            images.Add(new MediuxImage { AssetId = id + "-p", Slot = new ImageSlot(ImageSlotKind.Primary) });
        }

        if (hasBackdrop)
        {
            images.Add(new MediuxImage { AssetId = id + "-b", Slot = new ImageSlot(ImageSlotKind.Backdrop) });
        }

        foreach (var season in seasons)
        {
            images.Add(new MediuxImage
            {
                AssetId = $"{id}-s{season}",
                Slot = new ImageSlot(ImageSlotKind.SeasonPrimary, season)
            });
        }

        foreach (var (season, episode) in episodes)
        {
            images.Add(new MediuxImage
            {
                AssetId = $"{id}-s{season}e{episode}",
                Slot = new ImageSlot(ImageSlotKind.EpisodeTitleCard, season, episode)
            });
        }

        return new MediuxSet
        {
            Id = id,
            Username = user,
            Popularity = popularity,
            PopularityGlobal = popularity,
            Images = images
        };
    }
}

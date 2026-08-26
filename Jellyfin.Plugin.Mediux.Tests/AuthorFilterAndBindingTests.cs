using Jellyfin.Plugin.Mediux.Selection;

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

        var bindings = new SetBindings { Poster = "sticky", Backdrop = "sticky" };
        var result = SetSelector.Select([sticky, popular], needs, [], bindings);

        Assert.All(result.Preferred, i => Assert.Equal("sticky", i.SourceSet.Id));
        Assert.Equal("sticky", result.BindingUpdates[SetBindingKind.Poster]);
        Assert.Equal("sticky", result.BindingUpdates[SetBindingKind.Backdrop]);
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
        var bindings = new SetBindings { Poster = "gone", Backdrop = "gone" };

        var result = SetSelector.Select([remaining], needs, [], bindings);

        Assert.Equal(2, result.Preferred.Count);
        Assert.All(result.Preferred, i => Assert.Equal("new", i.SourceSet.Id));
        Assert.Equal("new", result.BindingUpdates[SetBindingKind.Poster]);
        Assert.Equal("new", result.BindingUpdates[SetBindingKind.Backdrop]);
    }

    [Fact]
    public void SetBindings_ApplyUpdates_IsPartial()
    {
        var bindings = new SetBindings
        {
            Poster = "old-poster",
            Backdrop = "old-backdrop",
            Titlecards = "old-cards"
        };

        bindings.ApplyUpdates(new Dictionary<SetBindingKind, string>
        {
            [SetBindingKind.Titlecards] = "new-cards",
            [SetBindingKind.Backdrop] = "new-backdrop"
        });

        Assert.Equal("old-poster", bindings.Poster);
        Assert.Equal("new-backdrop", bindings.Backdrop);
        Assert.Equal("new-cards", bindings.Titlecards);
    }

    [Fact]
    public void GetBindingKind_MapsSeasonZeroToSpecials()
    {
        Assert.Equal(SetBindingKind.SpecialsPoster, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.SeasonPrimary, 0)));
        Assert.Equal(SetBindingKind.SeasonPosters, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.SeasonPrimary, 1)));
        Assert.Equal(SetBindingKind.Titlecards, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.EpisodeTitleCard, 1, 2)));
        Assert.Equal(SetBindingKind.Logo, SetSelector.GetBindingKind(new ImageSlot(ImageSlotKind.Logo)));
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

using Jellyfin.Plugin.Tagsmith.Collections;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class MemberDiffTests
{
    private static readonly Guid _a = Guid.Parse("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Guid _b = Guid.Parse("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly Guid _c = Guid.Parse("cccccccccccccccccccccccccccccccc");

    [Fact]
    public void An_unchanged_collection_needs_no_writes()
    {
        // The steady state, and the reason the projection is cheap: a few dozen set
        // comparisons and nothing written.
        var change = MemberDiff.Between([_a, _b], [_a, _b]);

        Assert.True(change.IsEmpty);
        Assert.Empty(change.Add);
        Assert.Empty(change.Remove);
    }

    [Fact]
    public void Order_does_not_count_as_a_change() =>
        Assert.True(MemberDiff.Between([_a, _b, _c], [_c, _a, _b]).IsEmpty);

    [Fact]
    public void New_members_are_added()
    {
        var change = MemberDiff.Between([_a], [_a, _b]);

        Assert.Equal([_b], change.Add);
        Assert.Empty(change.Remove);
        Assert.False(change.IsEmpty);
    }

    [Fact]
    public void Retagged_items_are_removed()
    {
        var change = MemberDiff.Between([_a, _b], [_a]);

        Assert.Empty(change.Add);
        Assert.Equal([_b], change.Remove);
    }

    [Fact]
    public void Additions_and_removals_are_reported_together()
    {
        var change = MemberDiff.Between([_a, _b], [_b, _c]);

        Assert.Equal([_c], change.Add);
        Assert.Equal([_a], change.Remove);
    }

    [Fact]
    public void An_empty_collection_gains_everything()
    {
        var change = MemberDiff.Between([], [_a, _b]);

        Assert.Equal(2, change.Add.Count);
        Assert.Empty(change.Remove);
    }

    [Fact]
    public void A_value_with_no_items_left_loses_everything()
    {
        var change = MemberDiff.Between([_a, _b], []);

        Assert.Empty(change.Add);
        Assert.Equal(2, change.Remove.Count);
    }

    [Fact]
    public void Duplicates_on_either_side_are_collapsed()
    {
        var change = MemberDiff.Between([_a, _a], [_a, _a, _b]);

        Assert.Equal([_b], change.Add);
        Assert.Empty(change.Remove);
    }

    [Fact]
    public void Nothing_at_all_is_no_change() =>
        Assert.True(MemberDiff.Between([], []).IsEmpty);
}

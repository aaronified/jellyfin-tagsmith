using Jellyfin.Plugin.Tagsmith.Tagging;
using Xunit;

namespace Jellyfin.Plugin.Tagsmith.Tests;

public class TagAliasMapTests
{
    [Fact]
    public void Scoped_rule_rewrites_only_its_namespace()
    {
        var map = TagAliasMap.Parse(["origin:united_states => usa"]);

        Assert.Equal("origin=usa", map.Apply("origin=united_states", "="));
        Assert.Equal("lang=united_states", map.Apply("lang=united_states", "="));
    }

    [Fact]
    public void Global_rule_rewrites_any_namespace()
    {
        var map = TagAliasMap.Parse(["bengali => bangla"]);

        Assert.Equal("lang=bangla", map.Apply("lang=bengali", "="));
        Assert.Equal("original_lang=bangla", map.Apply("original_lang=bengali", "="));
    }

    [Fact]
    public void Empty_replacement_drops_the_tag() =>
        Assert.Null(TagAliasMap.Parse(["origin:unknown =>"]).Apply("origin=unknown", "="));

    [Fact]
    public void Scoped_rule_beats_global_rule()
    {
        var map = TagAliasMap.Parse(["united_states => us", "origin:united_states => usa"]);

        Assert.Equal("origin=usa", map.Apply("origin=united_states", "="));
        Assert.Equal("list=us", map.Apply("list=united_states", "="));
    }

    // ------------------------------------------------------------ structured values

    [Fact]
    public void An_award_value_keeps_its_colon_structure_through_a_scoped_rule()
    {
        // award= values are ceremony:category; a rule must be able to target them without
        // the colon being folded into an underscore by normalisation.
        var map = TagAliasMap.Parse(["award:oscar:best_picture => oscar:bp"]);

        Assert.Equal("award=oscar:bp", map.Apply("award=oscar:best_picture", "="));
        Assert.Equal("nomination=oscar:best_picture", map.Apply("nomination=oscar:best_picture", "="));
    }

    [Fact]
    public void A_structured_rule_normalises_each_segment()
    {
        var map = TagAliasMap.Parse(["award:Oscar:Best Picture => Oscar:BP"]);

        Assert.Equal("award=oscar:bp", map.Apply("award=oscar:best_picture", "="));
    }

    [Fact]
    public void A_structured_value_can_be_dropped()
    {
        var map = TagAliasMap.Parse(["nomination:emmy:best_drama_series =>"]);

        Assert.Null(map.Apply("nomination=emmy:best_drama_series", "="));
    }

    [Theory]
    [InlineData("# a comment")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no rule separator here")]
    [InlineData("=> orphaned")]
    public void Malformed_lines_are_ignored(string line) =>
        Assert.True(TagAliasMap.Parse([line]).IsEmpty);

    [Fact]
    public void Rules_are_normalised_on_both_sides()
    {
        var map = TagAliasMap.Parse(["Origin: United States of America => USA"]);

        Assert.Equal("origin=usa", map.Apply("origin=united_states_of_america", "="));
    }

    [Fact]
    public void Unmatched_tags_pass_through() =>
        Assert.Equal("origin=india", TagAliasMap.Parse(["origin:france => fr"]).Apply("origin=india", "="));

    [Fact]
    public void Honours_a_non_default_separator() =>
        Assert.Equal("origin:usa", TagAliasMap.Parse(["origin:united_states => usa"]).Apply("origin:united_states", ":"));
}

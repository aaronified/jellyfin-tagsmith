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

    // ------------------------------------------------------------ the shipped defaults

    [Fact]
    public void Urdu_folds_onto_Hindi_out_of_the_box()
    {
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules("lang", "audio_lang"));

        Assert.Equal("lang=hindi", map.Apply("lang=urdu", "="));
        Assert.Equal("audio_lang=hindi", map.Apply("audio_lang=urdu", "="));
    }

    [Fact]
    public void The_default_leaves_every_other_namespace_alone()
    {
        // Scoped, not global: nothing outside the two language namespaces is touched, and
        // Hindi itself is not rewritten to anything.
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules("lang", "audio_lang"));

        Assert.Equal("origin=urdu", map.Apply("origin=urdu", "="));
        Assert.Equal("lang=hindi", map.Apply("lang=hindi", "="));
        Assert.Equal("lang=punjabi", map.Apply("lang=punjabi", "="));
    }

    [Fact]
    public void A_user_rule_overrides_the_shipped_default()
    {
        // The documented way to turn the fold off: map the value to itself. Defaults are
        // parsed first so the settings page always gets the last word.
        var map = TagAliasMap.Parse(
            TagAliasMap.DefaultRules("lang", "audio_lang").Concat(["lang:urdu => urdu"]));

        Assert.Equal("lang=urdu", map.Apply("lang=urdu", "="));

        // Only the rule they overrode: audio_lang still folds. Two rules ship, so turning
        // the fold off completely takes two lines — which is what the docs now say.
        Assert.Equal("audio_lang=hindi", map.Apply("audio_lang=urdu", "="));
    }

    [Fact]
    public void Turning_the_fold_off_completely_takes_both_scoped_lines()
    {
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules("lang", "audio_lang")
            .Concat(["lang:urdu => urdu", "audio_lang:urdu => urdu"]));

        Assert.Equal("lang=urdu", map.Apply("lang=urdu", "="));
        Assert.Equal("audio_lang=urdu", map.Apply("audio_lang=urdu", "="));
    }

    [Theory]
    [InlineData("urdu => urdu")]
    [InlineData("urdu => hindustani")]
    [InlineData("urdu =>")]
    public void A_global_user_rule_cannot_override_the_shipped_scoped_rule(string userRule)
    {
        // Apply checks the scoped dictionary before the global one, so parse order is
        // irrelevant across the two. `urdu => urdu` is the line someone would reach for
        // first and it does nothing — hence the docs insisting on the scoped form.
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules("lang", "audio_lang").Concat([userRule]));

        Assert.Equal("lang=hindi", map.Apply("lang=urdu", "="));
    }

    [Fact]
    public void A_user_rule_can_redirect_the_default_somewhere_else()
    {
        var map = TagAliasMap.Parse(
            TagAliasMap.DefaultRules("lang", "audio_lang").Concat(["lang:urdu => hindustani"]));

        Assert.Equal("lang=hindustani", map.Apply("lang=urdu", "="));
    }

    [Fact]
    public void The_default_follows_a_renamed_namespace()
    {
        // Written against the configured namespace, so renaming `lang` in the settings does
        // not quietly disable the rule.
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules("language", "tracks"));

        Assert.Equal("language=hindi", map.Apply("language=urdu", "="));
        Assert.Equal("tracks=hindi", map.Apply("tracks=urdu", "="));
        Assert.Equal("lang=urdu", map.Apply("lang=urdu", "="));
    }

    [Theory]
    [InlineData("orig-lang")]
    [InlineData("orig lang")]
    [InlineData("audio.lang")]
    public void A_namespace_that_does_not_slug_to_itself_misses_its_scoped_rule(string tagNamespace)
    {
        // Pinning a known limitation rather than a desired behaviour: Parse slugs a rule's
        // scope (`orig-lang` -> `orig_lang`) while Apply matches the raw namespace, so the
        // two never meet. It bites any hand-written scoped rule identically, which is why
        // the fix would be in Apply rather than here. Documented in docs/tagging.md; if
        // this test starts failing, the limitation was fixed and the docs need updating.
        var map = TagAliasMap.Parse(TagAliasMap.DefaultRules(tagNamespace, null));

        Assert.Equal($"{tagNamespace}=urdu", map.Apply($"{tagNamespace}=urdu", "="));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    public void A_blank_namespace_produces_no_default_rule(string? language, string? audio) =>
        // A blank scope would parse as a global rule and rewrite `urdu` everywhere, which is
        // more than this default is entitled to do.
        Assert.True(TagAliasMap.Parse(TagAliasMap.DefaultRules(language, audio)).IsEmpty);
}

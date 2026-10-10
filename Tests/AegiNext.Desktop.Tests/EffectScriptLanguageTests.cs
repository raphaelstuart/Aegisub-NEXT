using AegiNext.Desktop.Controls;
using System.Globalization;

namespace AegiNext.Desktop.Tests;

public sealed class EffectScriptLanguageTests
{
    private static readonly string[] absolutePathValues = ["0", "1"];
    private static readonly string[] colorValues = ["base", "rgba(1, 1, 1, 1)"];

    [Fact]
    public void TokensPartitionSourceAndGiveDistinctSyntaxCategories()
    {
        const string SOURCE = "effect \"personal\" version 1\n# comment\nsegment enter fixed 300ms\n at 0 position offset(-20, 0) ease-out\nend";
        var tokens = EffectScriptLanguage.Tokenize(SOURCE);
        Assert.Equal(SOURCE, string.Concat(tokens.Select(token => SOURCE.Substring(token.Start, token.Length))));
        foreach (var expected in new[] { SyntaxTokenKind.KEYWORD, SyntaxTokenKind.PROPERTY, SyntaxTokenKind.NUMBER,
                     SyntaxTokenKind.STRING, SyntaxTokenKind.COMMENT, SyntaxTokenKind.FUNCTION, SyntaxTokenKind.INTERPOLATION })
        {
            Assert.Contains(tokens, token => token.Kind == expected);
        }
    }

    [Theory]
    [InlineData("", "effect \"my-effect\" version 1")]
    [InlineData("effect \"test\" version 1\n", "short-clip compress")]
    [InlineData("effect \"test\" version 1\nshort-clip compress\n", "segment stay flex 1")]
    [InlineData("effect \"test\" version 1\nshort-clip compress\nsegment enter fixed 300ms\n    at 0 po", "position")]
    [InlineData("at 0 position ", "offset(0, 0)")]
    [InlineData("at 0 opacity ", "offset(0)")]
    [InlineData("at 0 letter-s", "letter-spacing")]
    [InlineData("at 0 fill-b", "fill-blur")]
    [InlineData("at 0 stroke-b", "stroke-blur")]
    [InlineData("at 0 font-s", "font-size")]
    [InlineData("at 0 shadow-o", "shadow-offset")]
    [InlineData("at 0 shadow-b", "shadow-blur")]
    [InlineData("at 0 shadow-c", "shadow-color")]
    [InlineData("at 0 font-size ", "factor(1)")]
    [InlineData("at 0 shadow-offset ", "offset(0, 0)")]
    [InlineData("at 0 shadow-blur ", "base")]
    [InlineData("at 0 shadow-color ", "rgba(1, 1, 1, 1)")]
    [InlineData("at 0 letter-spacing ", "offset(0)")]
    [InlineData("at 0 fill-blur ", "factor(1)")]
    [InlineData("at 0 stroke-blur ", "base")]
    [InlineData("at 0 position offset(10, 20) ea", "ease-out")]
    [InlineData("at 0 fi", "fill")]
    [InlineData("at\t0\tpo", "position")]
    [InlineData("at\t0\tfill\t", "rgba(1, 1, 1, 1)")]
    [InlineData("at 0 stroke ", "rgba(1, 1, 1, 1)")]
    [InlineData("at 0 fill ", "base")]
    [InlineData("at 0 fill rgba(2.5, 0, 0, 0.5) ea", "ease-out")]
    [InlineData("at 0 mask-po", "mask-position")]
    [InlineData("at 0 mask-node", "mask-node(1,1).position")]
    [InlineData("at 0 mask-node(2,5).po", "mask-node(2,5).position")]
    [InlineData("at 0 mask-node(2, 5).in", "mask-node(2, 5).in-handle")]
    [InlineData("at 0 mask-node(1,1).position ", "offset(0, 0)")]
    [InlineData("at 0 mask-node(1, 2).out-handle ", "(0, 0)")]
    [InlineData("at 0 mask-scale ", "factor(1, 1)")]
    [InlineData("at 0 mask-rotation ", "offset(0)")]
    [InlineData("at 0 mask-position base po", "power(2)")]
    public void CompletionUsesTheCurrentGrammarAndReplacesOnlyThePartialWord(string source, string insertion)
    {
        var item = Assert.Single(EffectScriptLanguage.Complete(source, source.Length), value => value.Insertion == insertion);
        var result = source.Remove(item.Start, item.Length).Insert(item.Start, item.Insertion);
        Assert.EndsWith(insertion, result, StringComparison.Ordinal);
        Assert.Equal(source[..item.Start], result[..item.Start]);
    }

    [Theory]
    [InlineData("# at 0 po")]
    [InlineData("effect \"incomplete")]
    [InlineData("at 0 position offset(")]
    public void CommentsStringsAndIncompleteFunctionArgumentsDoNotOfferWrongGrammar(string source)
    {
        Assert.Empty(EffectScriptLanguage.Complete(source, source.Length));
    }

    [Fact]
    public void PathProgressNeverOffersUndefinedRelativeBaseValues()
    {
        const string SOURCE = "at 0 path-progress ";
        var completions = EffectScriptLanguage.Complete(SOURCE, SOURCE.Length);
        Assert.Equal(absolutePathValues, completions.Select(item => item.Insertion));
    }

    [Theory]
    [InlineData("fill")]
    [InlineData("stroke")]
    [InlineData("shadow-color")]
    public void ColorCompletionOnlyOffersSupportedRgbaAndBaseValues(string property)
    {
        var source = $"at 0 {property} ";
        var completions = EffectScriptLanguage.Complete(source, source.Length);
        Assert.Equal(colorValues, completions.Select(item => item.Insertion));
        Assert.DoesNotContain(completions, item => item.Insertion.StartsWith("offset", StringComparison.Ordinal) ||
            item.Insertion.StartsWith("factor", StringComparison.Ordinal));
        var syntax = $"at 0 {property} rgba(2.5, -0.1, 0.25, 0.5) ease-out";
        var tokens = EffectScriptLanguage.Tokenize(syntax);
        Assert.Equal(SyntaxTokenKind.PROPERTY, tokens.Single(token => syntax.Substring(token.Start, token.Length) == property).Kind);
        Assert.Equal(SyntaxTokenKind.FUNCTION, tokens.Single(token => syntax.Substring(token.Start, token.Length) == "rgba").Kind);
    }

    [Theory]
    [InlineData("letter-spacing")]
    [InlineData("fill-blur")]
    [InlineData("stroke-blur")]
    public void SubtitleAppearancePropertiesUseTheSharedMetadataForHighlighting(string property)
    {
        var source = $"at 0 {property} offset(2) linear";
        var token = Assert.Single(EffectScriptLanguage.Tokenize(source), value =>
            source.Substring(value.Start, value.Length) == property);
        Assert.Equal(SyntaxTokenKind.PROPERTY, token.Kind);
    }

    [Fact]
    public void NodeSelectorAndPowerHaveCompleteSyntaxCoverage()
    {
        const string SOURCE = "at 0 mask-node(2, 3).out-handle offset(5, -2) power(1.5)";
        var tokens = EffectScriptLanguage.Tokenize(SOURCE);

        Assert.Equal(SOURCE, string.Concat(tokens.Select(token => SOURCE.Substring(token.Start, token.Length))));
        Assert.Equal(SyntaxTokenKind.PROPERTY, tokens.Single(token => SOURCE.Substring(token.Start, token.Length) == "mask-node(2, 3).out-handle").Kind);
        Assert.Equal(SyntaxTokenKind.INTERPOLATION, tokens.Single(token => SOURCE.Substring(token.Start, token.Length) == "power").Kind);
    }

    [Fact]
    public void EscapedStringsKeepHashesInsideStringsAndStillPartitionTheSource()
    {
        const string SOURCE = """
            scope letters range(1,2)
                unit split("#", "\"", "\\", "\n") # real comment
                stagger 60ms
                segment pulse fixed 150ms repeat 2 pingpong
            """;

        var tokens = EffectScriptLanguage.Tokenize(SOURCE);

        Assert.Equal(SOURCE, string.Concat(tokens.Select(token => SOURCE.Substring(token.Start, token.Length))));
        var comment = Assert.Single(tokens, token => token.Kind == SyntaxTokenKind.COMMENT);
        Assert.Equal("# real comment", SOURCE.Substring(comment.Start, comment.Length));
        Assert.Equal(4, tokens.Count(token => token.Kind == SyntaxTokenKind.STRING));
        Assert.Contains(tokens, token => SOURCE.Substring(token.Start, token.Length) == "scope" && token.Kind == SyntaxTokenKind.KEYWORD);
        Assert.Contains(tokens, token => SOURCE.Substring(token.Start, token.Length) == "range" && token.Kind == SyntaxTokenKind.FUNCTION);
        Assert.Contains(tokens, token => SOURCE.Substring(token.Start, token.Length) == "split" && token.Kind == SyntaxTokenKind.FUNCTION);
        Assert.Contains(tokens, token => SOURCE.Substring(token.Start, token.Length) == "pingpong" && token.Kind == SyntaxTokenKind.KEYWORD);
    }

    [Theory]
    [InlineData("", "effect \"my-effect\" version 2")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\n", "scope letters current")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters ", "range(1,1)")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\n", "unit grapheme")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nunit ", "chunk(2)")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nunit ", "split(\"、\", \",\")")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\norder ", "reverse")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nstate ", "inactive")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nsegment wave fixed 150ms ", "repeat 1")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nsegment wave fixed 150ms ", "pingpong")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nsegment wave flex 1 ", "cycle 300ms")]
    [InlineData("effect \"test\" version 2\nshort-clip compress\nscope letters current\nsegment wave flex 1 cycle 300ms ", "pingpong")]
    public void VersionTwoCompletionOffersScopesUnitsAndSegmentModifiers(string source, string insertion)
    {
        Assert.Contains(EffectScriptLanguage.Complete(source, source.Length), item => item.Insertion == insertion);
    }

    [Fact]
    public void SegmentEndReturnsToItsScopeAndScopeEndReturnsToTheRoot()
    {
        const string SOURCE = """
            effect "test" version 2
            short-clip compress
            scope letters current
                unit grapheme
                segment wave fixed 300ms
                    at 0 scale base
                    at 1 scale base
                end

            """;

        var insideScope = EffectScriptLanguage.Complete(SOURCE, SOURCE.Length);

        Assert.Contains(insideScope, item => item.Insertion == "segment stay flex 1");
        Assert.Contains(insideScope, item => item.Insertion == "end");
        Assert.DoesNotContain(insideScope, item => item.Insertion.StartsWith("scope ", StringComparison.Ordinal) ||
            item.Insertion.StartsWith("unit ", StringComparison.Ordinal) || item.Insertion.StartsWith("at ", StringComparison.Ordinal));
        var closed = SOURCE + "end # close scope\n";
        var root = EffectScriptLanguage.Complete(closed, closed.Length);
        Assert.Contains(root, item => item.Insertion == "scope letters current");
        Assert.DoesNotContain(root, item => item.Insertion.StartsWith("segment ", StringComparison.Ordinal));
    }

    [Fact]
    public void QuotedHashesAndEscapedQuotesDoNotCorruptThePriorScopeStructure()
    {
        const string SOURCE = """
            effect "test" version 2
            short-clip compress
            scope letters current
                unit split("\"", "#") # end scope ignored
                sta
            """;

        var completions = EffectScriptLanguage.Complete(SOURCE, SOURCE.Length);

        Assert.Contains(completions, item => item.Insertion == "stagger 60ms");
        Assert.Contains(completions, item => item.Insertion == "state normal");
    }

    [Fact]
    public void UsedScopeFieldsAndWrongSegmentModifiersAreNotSuggested()
    {
        const string SOURCE = "effect \"test\" version 2\nshort-clip compress\nscope letters current\nunit grapheme\ndelay 0ms\nstagger 60ms\norder forward\nstate normal\n";
        var fields = EffectScriptLanguage.Complete(SOURCE, SOURCE.Length);
        Assert.DoesNotContain(fields, item => item.Insertion.StartsWith("unit ", StringComparison.Ordinal) ||
            item.Insertion.StartsWith("delay ", StringComparison.Ordinal) || item.Insertion.StartsWith("stagger ", StringComparison.Ordinal) ||
            item.Insertion.StartsWith("order ", StringComparison.Ordinal) || item.Insertion.StartsWith("state ", StringComparison.Ordinal));
        var fixedSource = SOURCE + "segment wave fixed 300ms repeat 2 pingpong ";
        Assert.Empty(EffectScriptLanguage.Complete(fixedSource, fixedSource.Length));
        var flexSource = SOURCE + "segment wave flex 1 ";
        Assert.DoesNotContain(EffectScriptLanguage.Complete(flexSource, flexSource.Length), item => item.Insertion.StartsWith("repeat", StringComparison.Ordinal) || item.Insertion == "pingpong");
        var v1Source = "effect \"test\" version 1\nshort-clip compress\nsegment wave fixed 300ms ";
        Assert.Empty(EffectScriptLanguage.Complete(v1Source, v1Source.Length));
    }

    [Theory]
    [InlineData("unit split(\"#")]
    [InlineData("unit split(\"\\\"#")]
    [InlineData("unit split(\"#\") # comment")]
    public void StringAndCommentCaretsDoNotOfferCompletion(string source)
    {
        Assert.Empty(EffectScriptLanguage.Complete(source, source.Length));
    }

    [Fact]
    public void RangeAndVisualStatePropertyCompletionUsesTheTargetMetadata()
    {
        const string RANGE = "effect \"test\" version 2\nshort-clip compress\nscope letters current\nunit grapheme\nsegment stay flex 1\nat 0 ";
        var range = EffectScriptLanguage.Complete(RANGE, RANGE.Length);
        Assert.Contains(range, item => item.Insertion == "position");
        Assert.DoesNotContain(range, item => item.Insertion is "opacity" or "blur" or "mask-position" or "path-progress");
        var visual = RANGE.Replace("unit grapheme\n", "unit grapheme\nstate active\n", StringComparison.Ordinal);
        var active = EffectScriptLanguage.Complete(visual, visual.Length);
        Assert.Contains(active, item => item.Insertion == "fill");
        Assert.DoesNotContain(active, item => item.Insertion is "position" or "scale" or "font-size");
    }
}

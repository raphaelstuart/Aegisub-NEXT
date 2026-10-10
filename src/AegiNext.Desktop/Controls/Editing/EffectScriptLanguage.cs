using System.Text.RegularExpressions;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Desktop.I18n;

namespace AegiNext.Desktop.Controls;

internal static class EffectScriptLanguage
{
    private static readonly Regex tokens = new("#[^\\r\\n]*|\"(?:\\\\[^\\r\\n]|[^\"\\\\\\r\\n])*\"?|mask-node\\(\\s*\\d+\\s*,\\s*\\d+\\s*\\)\\.(?:position|in-handle|out-handle)|[+-]?(?:\\d+(?:\\.\\d+)?|\\.\\d+)(?:ms|s)?|[a-z][a-z-]*|\\s+|.",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex keyPrefix = new(@"^\s*at\s+\S+\s+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex selectorWhitespace = new(@"mask-node\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> keywords =
    [
        "effect", "version", "short-clip", "compress", "reject", "scope", "current", "subtitle", "unit", "group", "grapheme", "word", "line", "paragraph",
        "delay", "stagger", "order", "forward", "reverse", "state", "normal", "active", "inactive", "segment", "fixed", "flex", "repeat", "pingpong", "cycle", "at", "end"
    ];
    private static readonly HashSet<string> scopeFields = ["unit", "delay", "stagger", "order", "state"];
    private static readonly string[] units = ["group", "grapheme", "chunk(2)", "word", "line", "paragraph", "split(\"、\", \",\")"];
    private static readonly string[] interpolation = ["linear", "hold", "ease-in", "ease-out", "ease-in-out", "power(2)"];

    internal static IReadOnlyList<SyntaxToken> Tokenize(string source)
    {
        var result = new List<SyntaxToken>();
        foreach (Match match in tokens.Matches(source))
        {
            var value = match.Value;
            var kind = value[0] switch
            {
                '#' => SyntaxTokenKind.COMMENT,
                '"' => SyntaxTokenKind.STRING,
                _ when keywords.Contains(value) => SyntaxTokenKind.KEYWORD,
                _ when EffectScriptPropertyMetadata.TryGetProperty(value, out _) || value == "mask-node" => SyntaxTokenKind.PROPERTY,
                _ when value is "base" or "offset" or "factor" or "rgba" or "range" or "chunk" or "split" => SyntaxTokenKind.FUNCTION,
                _ when interpolation.Contains(value, StringComparer.Ordinal) || value == "power" => SyntaxTokenKind.INTERPOLATION,
                _ when char.IsDigit(value[0]) || value[0] is '+' or '-' or '.' && value.Length > 1 => SyntaxTokenKind.NUMBER,
                _ => SyntaxTokenKind.TEXT
            };
            result.Add(new(match.Index, match.Length, kind));
        }

        return result;
    }

    internal static IReadOnlyList<EffectScriptCompletion> Complete(string source, int caret)
    {
        caret = Math.Clamp(caret, 0, source.Length);
        var lineStart = source.LastIndexOf('\n', Math.Max(0, caret - 1), Math.Min(caret, source.Length));
        lineStart++;
        var prefix = source[lineStart..caret];
        if (StripComment(prefix, out var inString).Length != prefix.Length || inString)
        {
            return [];
        }
        var context = ReadContext(source[..lineStart]);

        var keyStart = keyPrefix.Match(prefix);
        if (keyStart.Success)
        {
            var propertyStart = keyStart.Length;
            var depth = 0;
            var end = propertyStart;
            for (; end < prefix.Length; end++)
            {
                var character = prefix[end];
                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    depth--;
                }
                else if (char.IsWhiteSpace(character) && depth == 0)
                {
                    break;
                }
            }

            if (end == prefix.Length)
            {
                var partialProperty = prefix[propertyStart..];
                var propertyCandidates = PropertyCandidates(context);
                var closing = partialProperty.IndexOf(')', StringComparison.Ordinal);
                if (partialProperty.StartsWith("mask-node(", StringComparison.Ordinal) && closing >= 0)
                {
                    var stem = partialProperty[..(closing + 1)] + ".";
                    propertyCandidates = context.TextRange || context.State != SubtitleAnimationState.NORMAL ? [] :
                        [stem + "position", stem + "in-handle", stem + "out-handle"];
                }

                return propertyCandidates.Where(name => name.StartsWith(partialProperty, StringComparison.Ordinal))
                    .Select(name => new EffectScriptCompletion(lineStart + propertyStart, partialProperty.Length, name, name,
                        ValueHint(name))).ToArray();
            }
        }

        var replacementStart = caret;
        while (replacementStart > lineStart && (char.IsLetter(source[replacementStart - 1]) || source[replacementStart - 1] == '-'))
        {
            replacementStart--;
        }

        var partial = source[replacementStart..caret];
        var before = source[lineStart..replacementStart].Trim();
        var words = selectorWhitespace.Replace(before, "mask-node($1,$2)")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = new List<(string Text, string Hint)>();
        if (words.Length == 0)
        {
            if (!context.HasHeader)
            {
                candidates.Add(("effect \"my-effect\" version 1", Localization.Get("Settings.ScriptHintHeader")));
                candidates.Add(("effect \"my-effect\" version 2", Localization.Get("Settings.ScriptHintHeaderV2")));
            }
            else
            {
                if (context.Block == "segment")
                {
                    candidates.Add(("at 0 ", Localization.Get("Settings.ScriptHintAt")));
                    candidates.Add(("at 1 ", Localization.Get("Settings.ScriptHintEndpoints")));
                    candidates.Add(("end", Localization.Get("Settings.ScriptHintEnd")));
                }
                else if (!context.HasShortClip)
                {
                    candidates.Add(("short-clip compress", Localization.Get("Settings.ScriptHintCompress")));
                    candidates.Add(("short-clip reject", Localization.Get("Settings.ScriptHintReject")));
                }
                else if (context.VersionTwo && context.Block is null)
                {
                    candidates.Add(("scope letters current", Localization.Get("Settings.ScriptHintScope")));
                    candidates.Add(("scope whole subtitle", Localization.Get("Settings.ScriptHintScope")));
                    candidates.Add(("scope part range(1,1)", Localization.Get("Settings.ScriptHintScope")));
                }
                else
                {
                    if (context.VersionTwo && !context.ScopeHasSegments)
                    {
                        AddScopeFields(candidates, context);
                    }
                    candidates.Add(("segment enter fixed 300ms", Localization.Get("Settings.ScriptHintFixed")));
                    candidates.Add(("segment stay flex 1", Localization.Get("Settings.ScriptHintFlex")));
                    if (context.VersionTwo)
                    {
                        candidates.Add(("end", Localization.Get("Settings.ScriptHintScopeEnd")));
                    }
                }
            }
        }
        else if (words[0] == "short-clip")
        {
            candidates.Add(("compress", Localization.Get("Settings.ScriptHintCompress")));
            candidates.Add(("reject", Localization.Get("Settings.ScriptHintReject")));
        }
        else if (context.VersionTwo && words[0] == "scope" && words.Length == 2 && context.Block is null)
        {
            candidates.Add(("current", Localization.Get("Settings.ScriptHintTarget")));
            candidates.Add(("subtitle", Localization.Get("Settings.ScriptHintTarget")));
            candidates.Add(("range(1,1)", Localization.Get("Settings.ScriptHintTarget")));
        }
        else if (context.VersionTwo && context.Block == "scope" && !context.ScopeHasSegments && words.Length == 1 &&
                 !context.ScopeFields.Contains(words[0]))
        {
            AddScopeFieldValues(candidates, words[0]);
        }
        else if (words[0] == "segment" && words.Length == 2 && (!context.VersionTwo || context.Block == "scope"))
        {
            candidates.Add(("fixed 300ms", Localization.Get("Settings.ScriptHintFixed")));
            candidates.Add(("flex 1", Localization.Get("Settings.ScriptHintFlex")));
        }
        else if (context.VersionTwo && words[0] == "segment" && words.Length >= 4 && context.Block == "scope")
        {
            AddSegmentModifiers(candidates, words);
        }
        else if (words[0] == "at" && words.Length == 2)
        {
            candidates.AddRange(PropertyCandidates(context).Select(value => (value, ValueHint(value))));
        }
        else if (words[0] == "at" && words.Length == 3)
        {
            var vector = EffectScriptPropertyMetadata.TryGetProperty(words[2], out var property) &&
                AnimationPropertyMetadata.GetValueKind(EffectScriptPropertyMetadata.GetAnimationProperty(property)) == AnimationValueKind.VECTOR;
            if (EffectScriptPropertyMetadata.TryGetProperty(words[2], out var colorProperty) &&
                AnimationPropertyMetadata.GetValueKind(EffectScriptPropertyMetadata.GetAnimationProperty(colorProperty)) == AnimationValueKind.COLOR)
            {
                candidates.Add(("base", Localization.Get("Settings.ScriptHintBase")));
                candidates.Add(("rgba(1, 1, 1, 1)", Localization.Get("Settings.ScriptHintColor")));
            }
            else if (words[2] == "path-progress")
            {
                candidates.Add(("0", Localization.Get("Settings.ScriptHintPath")));
                candidates.Add(("1", Localization.Get("Settings.ScriptHintPath")));
            }
            else
            {
                candidates.Add(("base", Localization.Get("Settings.ScriptHintBase")));
                candidates.Add((vector ? "offset(0, 0)" : "offset(0)", Localization.Get("Settings.ScriptHintOffset")));
                candidates.Add((vector ? "factor(1, 1)" : "factor(1)", Localization.Get("Settings.ScriptHintFactor")));
                candidates.Add((vector ? "(0, 0)" : "0", Localization.Get("Settings.ScriptHintAbsolute")));
            }
        }
        else if (words[0] == "at" && words.Length >= 4 && (!before.Contains('(', StringComparison.Ordinal) || before.Contains(')', StringComparison.Ordinal)))
        {
            candidates.AddRange(interpolation.Select(value => (value, Localization.Get("Settings.ScriptHintInterpolation"))));
        }

        return candidates.Where(item => item.Text.StartsWith(partial, StringComparison.Ordinal))
            .Select(item => new EffectScriptCompletion(replacementStart, caret - replacementStart, item.Text, item.Text, item.Hint)).ToArray();
    }

    private static string StripComment(string line, out bool inString)
    {
        inString = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inString && character == '\\')
            {
                index++;
            }
            else if (character == '"')
            {
                inString = !inString;
            }
            else if (!inString && character == '#')
            {
                return line[..index];
            }
        }
        return line;
    }

    private static EffectScriptLanguageContext ReadContext(string source)
    {
        var blocks = new List<string>();
        var fields = new HashSet<string>();
        var versionTwo = false;
        var hasHeader = false;
        var hasShortClip = false;
        var hasSegments = false;
        var explicitRange = false;
        var textRange = false;
        var state = SubtitleAnimationState.NORMAL;
        foreach (var raw in source.Split('\n'))
        {
            var line = StripComment(raw, out _).Trim().TrimStart('\uFEFF');
            var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                continue;
            }
            if (words[0] == "effect")
            {
                hasHeader = true;
                versionTwo = words.Length >= 3 && words[^2] == "version" && words[^1] == "2";
            }
            else if (words[0] == "short-clip")
            {
                hasShortClip = true;
            }
            else if (versionTwo && words[0] == "scope" && blocks.Count == 0)
            {
                blocks.Add("scope");
                fields.Clear();
                hasSegments = false;
                explicitRange = words.Length >= 3 && words[2].StartsWith("range(", StringComparison.Ordinal);
                textRange = explicitRange;
                state = SubtitleAnimationState.NORMAL;
            }
            else if (words[0] == "segment" && (blocks.Count == 0 && !versionTwo || blocks.Count > 0 && blocks[^1] == "scope"))
            {
                blocks.Add("segment");
                hasSegments = true;
            }
            else if (words[0] == "end" && words.Length == 1 && blocks.Count > 0)
            {
                var closing = blocks[^1];
                blocks.RemoveAt(blocks.Count - 1);
                if (closing == "scope")
                {
                    fields.Clear();
                    hasSegments = false;
                    textRange = false;
                    state = SubtitleAnimationState.NORMAL;
                }
            }
            else if (blocks.Count > 0 && blocks[^1] == "scope" && scopeFields.Contains(words[0]))
            {
                fields.Add(words[0]);
                if (words.Length >= 2 && words[0] == "unit")
                {
                    textRange = explicitRange || words[1] != "group";
                }
                else if (words.Length >= 2 && words[0] == "state")
                {
                    state = words[1] switch
                    {
                        "active" => SubtitleAnimationState.ACTIVE,
                        "inactive" => SubtitleAnimationState.INACTIVE,
                        _ => SubtitleAnimationState.NORMAL
                    };
                }
            }
        }
        return new(versionTwo, hasHeader, hasShortClip, blocks.Count == 0 ? null : blocks[^1], fields, hasSegments, textRange, state);
    }

    private static IEnumerable<string> PropertyCandidates(EffectScriptLanguageContext context)
    {
        return EffectScriptPropertyMetadata.PropertyNames.Where(name =>
        {
            EffectScriptPropertyMetadata.TryGetProperty(name, out var property);
            var animationProperty = EffectScriptPropertyMetadata.GetAnimationProperty(property);
            return (!context.TextRange || AnimationPropertyMetadata.IsTextRangeProperty(animationProperty)) &&
                   (context.State == SubtitleAnimationState.NORMAL || AnimationPropertyMetadata.IsSubtitleVisualProperty(animationProperty));
        });
    }

    private static void AddScopeFields(List<(string Text, string Hint)> candidates, EffectScriptLanguageContext context)
    {
        if (!context.ScopeFields.Contains("unit"))
        {
            candidates.Add(("unit grapheme", Localization.Get("Settings.ScriptHintUnit")));
            candidates.Add(("unit group", Localization.Get("Settings.ScriptHintUnit")));
        }
        if (!context.ScopeFields.Contains("delay"))
        {
            candidates.Add(("delay 0ms", Localization.Get("Settings.ScriptHintDelay")));
        }
        if (!context.ScopeFields.Contains("stagger"))
        {
            candidates.Add(("stagger 60ms", Localization.Get("Settings.ScriptHintStagger")));
        }
        if (!context.ScopeFields.Contains("order"))
        {
            candidates.Add(("order forward", Localization.Get("Settings.ScriptHintOrder")));
        }
        if (!context.ScopeFields.Contains("state"))
        {
            candidates.Add(("state normal", Localization.Get("Settings.ScriptHintState")));
        }
    }

    private static void AddScopeFieldValues(List<(string Text, string Hint)> candidates, string field)
    {
        switch (field)
        {
            case "unit":
                candidates.AddRange(units.Select(value => (value, Localization.Get("Settings.ScriptHintUnit"))));
                break;
            case "delay":
                candidates.Add(("0ms", Localization.Get("Settings.ScriptHintDelay")));
                candidates.Add(("100ms", Localization.Get("Settings.ScriptHintDelay")));
                break;
            case "stagger":
                candidates.Add(("60ms", Localization.Get("Settings.ScriptHintStagger")));
                candidates.Add(("300ms", Localization.Get("Settings.ScriptHintStagger")));
                break;
            case "order":
                candidates.Add(("forward", Localization.Get("Settings.ScriptHintOrder")));
                candidates.Add(("reverse", Localization.Get("Settings.ScriptHintOrder")));
                break;
            case "state":
                candidates.Add(("normal", Localization.Get("Settings.ScriptHintState")));
                candidates.Add(("active", Localization.Get("Settings.ScriptHintState")));
                candidates.Add(("inactive", Localization.Get("Settings.ScriptHintState")));
                break;
        }
    }

    private static void AddSegmentModifiers(List<(string Text, string Hint)> candidates, string[] words)
    {
        if (words[^1] == "repeat" && words[2] == "fixed")
        {
            candidates.Add(("1", Localization.Get("Settings.ScriptHintRepeat")));
        }
        else if (words[^1] == "cycle" && words[2] == "flex")
        {
            candidates.Add(("300ms", Localization.Get("Settings.ScriptHintCycle")));
        }
        else if (words[2] == "fixed")
        {
            if (!words.Contains("repeat", StringComparer.Ordinal))
            {
                candidates.Add(("repeat 1", Localization.Get("Settings.ScriptHintRepeat")));
            }
            if (!words.Contains("pingpong", StringComparer.Ordinal))
            {
                candidates.Add(("pingpong", Localization.Get("Settings.ScriptHintPingPong")));
            }
        }
        else if (words[2] == "flex")
        {
            if (!words.Contains("cycle", StringComparer.Ordinal))
            {
                candidates.Add(("cycle 300ms", Localization.Get("Settings.ScriptHintCycle")));
            }
            else if (!words.Contains("pingpong", StringComparer.Ordinal))
            {
                candidates.Add(("pingpong", Localization.Get("Settings.ScriptHintPingPong")));
            }
        }
    }

    private static string ValueHint(string propertyName)
    {
        var kind = EffectScriptPropertyMetadata.TryGetProperty(propertyName, out var property)
            ? AnimationPropertyMetadata.GetValueKind(EffectScriptPropertyMetadata.GetAnimationProperty(property)) : AnimationValueKind.SCALAR;
        return Localization.Get("Settings." + (kind switch
        {
            AnimationValueKind.VECTOR => "ScriptHintVector",
            AnimationValueKind.COLOR => "ScriptHintColor",
            _ => "ScriptHintScalar"
        }));
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;

namespace AegiNext.Application.SubtitleFormats;

/// <summary>ASS v4+ 交换；将不支持的效果列为损失，拒绝非法结构和时间。</summary>
public static class AssSubtitleFormat
{
    /// <summary>以目标画布重采样静态字幕布局，保留对白来源的稳定顺序。</summary>
    public static AssImportResult Parse(string source, int targetWidth = 1920, int targetHeight = 1080,
        IAssFontWeightResolver? fontWeightResolver = null)
    {
        AssFormatValues.CheckText(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetHeight);
        var rows = source.TrimStart('\uFEFF').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styleRows = new List<Dictionary<string, string>>();
        var events = new List<Dictionary<string, string>>();
        var section = string.Empty;
        string[]? styleFormat = null;
        string[]? eventFormat = null;
        var sawInfo = false;
        var sawEvents = false;
        var attachments = false;
        foreach (var row in rows)
        {
            var trimmed = row.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';'))
            {
                continue;
            }
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed;
                sawInfo |= section.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase);
                sawEvents |= section.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                attachments |= section.Equals("[Fonts]", StringComparison.OrdinalIgnoreCase) || section.Equals("[Graphics]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!section.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase) && !section.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase) && !section.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var content = row.TrimStart();
            var colon = content.IndexOf(':');
            if (colon < 0)
            {
                throw new InvalidDataException("ASS 行缺少字段分隔符。");
            }
            var key = content[..colon].Trim();
            var value = content[(colon + 1)..].TrimStart();
            if (section.Equals("[Script Info]", StringComparison.OrdinalIgnoreCase))
            {
                info[key] = value;
            }
            else if (section.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    styleFormat = ParseFormat(value);
                }
                else if (key.Equals("Style", StringComparison.OrdinalIgnoreCase))
                {
                    styleRows.Add(ParseFields(value, styleFormat ?? throw new InvalidDataException("ASS 样式缺少 Format。")));
                }
            }
            else if (section.Equals("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                if (key.Equals("Format", StringComparison.OrdinalIgnoreCase))
                {
                    eventFormat = ParseFormat(value);
                    if (!eventFormat[^1].Equals("Text", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("ASS Text 必须是最后一个对白字段。");
                    }
                }
                else if (key.Equals("Dialogue", StringComparison.OrdinalIgnoreCase))
                {
                    if (events.Count >= 100000)
                    {
                        throw new InvalidDataException("ASS 对白数量超过预算。");
                    }
                    events.Add(ParseFields(value, eventFormat ?? throw new InvalidDataException("ASS 对白缺少 Format。")));
                }
            }
        }
        if (!sawInfo || !sawEvents || info.TryGetValue("ScriptType", out var scriptType) && !scriptType.Equals("v4.00+", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("仅支持含 Script Info 和 Events 的 ASS v4+ 文件。");
        }
        var hasWidth = info.TryGetValue("PlayResX", out var x);
        var hasHeight = info.TryGetValue("PlayResY", out var y);
        var width = hasWidth ? AssFormatValues.Integer(x!) : targetWidth;
        var height = hasHeight ? AssFormatValues.Integer(y!) : targetHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("ASS PlayRes 必须为正。");
        }
        var scaleX = (double)targetWidth / width;
        var scaleY = (double)targetHeight / height;
        var diagnostics = ImmutableArray.CreateBuilder<SubtitleFormatDiagnostic>();
        var colorMatrix = Get(info, "YCbCr Matrix", string.Empty).Trim().ToUpperInvariant();
        if (colorMatrix is "TV.601" or "PC.601" or "TV.709" or "PC.709" or "TV.240M" or "PC.240M" or "TV.FCC" or "PC.FCC")
        {
            diagnostics.Add(new("Ass.YCbCrMatrix", $"ASS 的 YCbCr Matrix ({colorMatrix}) 色彩匹配信息未导入；项目保留 RGB 数值，源播放器若依据视频色彩空间进行匹配，颜色可能不同。"));
        }
        var wrapStyle = AssFormatValues.Integer(Get(info, "WrapStyle", "0"));
        if (wrapStyle is < 0 or > 3)
        {
            diagnostics.Add(new("Ass.WrapStyle", "ASS WrapStyle 须为 0 至 3，已采用默认智能换行模式 0。"));
            wrapStyle = 0;
        }
        var layoutWidth = AssFormatValues.Integer(Get(info, "LayoutResX", "0"));
        var layoutHeight = AssFormatValues.Integer(Get(info, "LayoutResY", "0"));
        var blurUsesPlayRes = layoutWidth <= 0 || layoutHeight <= 0;
        var blurScaleX = (double)targetWidth / (blurUsesPlayRes ? width : layoutWidth);
        var blurScaleY = (double)targetHeight / (blurUsesPlayRes ? height : layoutHeight);
        var scaledBorder = !Get(info, "ScaledBorderAndShadow", "yes").Equals("no", StringComparison.OrdinalIgnoreCase);
        var resolution = scaledBorder ? new AssResolutionContext(scaleX, scaleY) :
            blurUsesPlayRes ? new(1, 1) : new((double)targetWidth / layoutWidth, (double)targetHeight / layoutHeight);
        if (!scaledBorder && blurUsesPlayRes)
        {
            diagnostics.Add(new("Ass.BorderLayoutResolution", "ASS 未提供完整有效的 LayoutRes，未缩放描边与阴影按目标画布像素解释；原视频尺寸不同时外观可能不同。"));
        }
        var styles = new Dictionary<string, AssStyleDefinition>(StringComparer.Ordinal);
        var unsupportedGeometry = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fields in styleRows)
        {
            var style = ParseStyle(fields, scaleX, scaleY, wrapStyle, resolution, diagnostics);
            if (!styles.TryAdd(style.Name, style))
            {
                throw new InvalidDataException("ASS 样式名称重复。");
            }
            if (Get(fields, "BorderStyle", "1") != "1")
            {
                unsupportedGeometry.Add(style.Name);
            }
        }
        var fallback = new AssStyleDefinition("Default", new()
        {
            FontSize = 20 * scaleY, ShadowBlur = 0,
            WrapMode = wrapStyle == 2 ? SubtitleWrapMode.NO_WRAP : SubtitleWrapMode.NATURAL
        }, SceneColor.White);
        var lines = ImmutableArray.CreateBuilder<SubtitleLine>();
        var clips = ImmutableArray.CreateBuilder<SubtitleClipImport>();
        if (!hasWidth || !hasHeight)
        {
            diagnostics.Add(new("Ass.PlayRes", "ASS 缺失的 PlayRes 轴按目标画布尺寸解释，已声明的轴仍按其源尺寸重采样。"));
        }
        if (attachments)
        {
            diagnostics.Add(new("Ass.Attachments", "ASS 嵌入字体或图片未导入，请在项目中单独添加资源。"));
        }
        foreach (var fields in events.OrderBy(row => AssFormatValues.Integer(Get(row, "Layer", "0"))))
        {
            var name = Get(fields, "Style", "Default");
            ProjectValidator.ValidateSubtitleStyleName(name);
            var definition = styles.TryGetValue(name, out var declared) ? declared : fallback;
            var start = AssFormatValues.ParseTime(Required(fields, "Start"));
            var end = AssFormatValues.ParseTime(Required(fields, "End"));
            if (start >= end)
            {
                throw new InvalidDataException("ASS 结束时间必须晚于开始时间。");
            }
            var line = new SubtitleLine { Start = start, End = end, Style = definition.Style, StyleName = name };
            var marginL = AssFormatValues.Number(Get(fields, "MarginL", "0"));
            var marginR = AssFormatValues.Number(Get(fields, "MarginR", "0"));
            var marginV = AssFormatValues.Number(Get(fields, "MarginV", "0"));
            if (marginL != 0 || marginR != 0 || marginV != 0)
            {
                line = line with
                {
                    Style = line.Style with
                    {
                        Margins = new(
                            marginL == 0 ? line.Style.Margins.Left : marginL * scaleX,
                            marginR == 0 ? line.Style.Margins.Right : marginR * scaleX,
                            marginV == 0 ? line.Style.Margins.Vertical : marginV * scaleY)
                    }
                };
            }
            var parsed = new AssTextParser(line, styles, definition.Secondary, scaleX, scaleY, canvasWidth: targetWidth,
                canvasHeight: targetHeight, wrapStyle: wrapStyle, blurScaleX: blurScaleX, blurScaleY: blurScaleY,
                blurUsesPlayRes: blurUsesPlayRes, resolution: resolution, fontWeightResolver: fontWeightResolver,
                dialogueMargins: new(marginL * scaleX, marginR * scaleX, marginV * scaleY)).Parse(Required(fields, "Text"));
            var importedLine = parsed.Line;
            lines.Add(importedLine);
            clips.Add(new(importedLine, parsed.Mask, parsed.MaskTracks.AddRange(parsed.PlacementTracks).AddRange(parsed.OpacityTracks).AddRange(parsed.NumericTracks), parsed.ContentOffset)
            {
                Transform = parsed.Transform
            });
            diagnostics.AddRange(parsed.Diagnostics);
            if (!styles.ContainsKey(name))
            {
                diagnostics.Add(new("Ass.UnknownStyle", $"样式 {name} 不存在，采用默认样式。", SubtitleId: line.Id));
            }
            if (Get(fields, "Effect", "").Length > 0)
            {
                diagnostics.Add(new("Ass.Effect", "ASS 对白 Effect 字段未导入。", SubtitleId: line.Id));
            }
            if (unsupportedGeometry.Contains(name))
            {
                diagnostics.Add(new("Ass.StyleGeometry", "ASS 样式的背景框未导入。", SubtitleId: line.Id));
            }
        }
        return new(lines.ToImmutable(), diagnostics.ToImmutable()) { Clips = clips.ToImmutable() };
    }

    /// <summary>按工程合成层顺序导出全部字幕，静态样式去重，时间显式量化到厘秒。</summary>
    public static SubtitleFormatWriteResult Write(ProjectDocument document, MediaTime timeOffset = default,
        ISubtitlePlacementMeasurer? placementMeasurer = null)
    {
        ProjectValidator.Validate(document);
        var result = new StringBuilder();
        result.AppendLine("[Script Info]").AppendLine("ScriptType: v4.00+")
            .AppendLine("PlayResX: " + document.Width.ToString(CultureInfo.InvariantCulture))
            .AppendLine("PlayResY: " + document.Height.ToString(CultureInfo.InvariantCulture))
            .AppendLine("LayoutResX: " + document.Width.ToString(CultureInfo.InvariantCulture))
            .AppendLine("LayoutResY: " + document.Height.ToString(CultureInfo.InvariantCulture))
            .AppendLine("YCbCr Matrix: None").AppendLine("WrapStyle: 1").AppendLine("ScaledBorderAndShadow: yes").AppendLine();
        result.AppendLine("[V4+ Styles]").AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        var diagnostics = ImmutableArray.CreateBuilder<SubtitleFormatDiagnostic>();
        var styles = AssStyleTable.Write(document.Subtitles, result, diagnostics);
        var reportedDiagnostics = new HashSet<(Guid? SubtitleId, string Code)>();
        result.AppendLine().AppendLine("[Events]").AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        var byId = document.Subtitles.ToDictionary(line => line.Id);
        var order = 0;
        var exportedCount = 0;
        foreach (var layer in new ProjectClipIndex(document).LayersInDrawingOrder.Where(layer => layer.SubtitleId.HasValue))
        {
            var line = byId[layer.SubtitleId!.Value];
            if (line.Start + timeOffset < MediaTime.Zero)
            {
                throw new InvalidDataException($"ASS 字幕 {line.Id} 的外部开始时间 {line.Start + timeOffset} 为负；请调整区间或播放零点，不会自动裁剪。");
            }
            if (line.Style.FontAssetId.HasValue)
            {
                diagnostics.Add(new("Ass.FontResource", "ASS 文件不包含项目嵌入字体，请在播放环境安装对应字体。", SubtitleId: line.Id));
            }
            if (line.Style.TextAlign is { } textAlign && (int)textAlign != (int)line.Style.Alignment % 3)
            {
                diagnostics.Add(new("Ass.TextAlign", "ASS 九宫格定位不能独立保留项目文字对齐，导出时采用定位对应的文字对齐。", SubtitleId: line.Id));
            }
            if (!line.Style.LineHeight.Equals(1.2))
            {
                diagnostics.Add(new("Ass.LineHeight", "ASS 不支持项目自定义行高。", SubtitleId: line.Id));
            }
            AssExportPrecision.AddStyle(line.Style, line.Id, diagnostics, includeBlur: false);
            AssExportPrecision.AddTime(line.Start + timeOffset, line.End + timeOffset, line.Id, diagnostics);
            if (layer.Blur > 0)
            {
                diagnostics.Add(new("Ass.LayerBlur", "ASS 文字边缘模糊不能表达项目整层模糊，导出时已省略整层模糊。", SubtitleId: line.Id));
            }
            var conversion = new AssEventConversionContext(document, layer, line, placementMeasurer, diagnostics);
            foreach (var sample in AssMaskSampling.Samples(document, layer, line, diagnostics, timeOffset))
            {
                if (exportedCount++ >= 100000)
                {
                    throw new InvalidDataException("ASS 蒙版展开后的总对白数量超过 100,000 条预算。");
                }
                var sampleLine = line with { Start = sample.Start, End = sample.End };
                var body = AssTextWriter.Write(sampleLine, sample.ContentTime, conversion: conversion,
                    eventOrigin: conversion.EventOrigin(sample, timeOffset));
                foreach (var diagnostic in body.Diagnostics)
                {
                    if (reportedDiagnostics.Add((diagnostic.SubtitleId, diagnostic.Code)))
                    {
                        diagnostics.Add(diagnostic);
                    }
                }
                var placement = conversion.PlacementTags(sample, timeOffset);
                var opacity = conversion.OpacityTags(sample, timeOffset);
                var maskTags = sample.Tags.Length == 0 ? string.Empty : "{" + sample.Tags + "}";
                result.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"Dialogue: {order},{AssFormatValues.Time(sample.Start + timeOffset, MediaTimeRounding.FLOOR)},{AssFormatValues.Time(sample.End + timeOffset, MediaTimeRounding.CEILING)},{styles[line.Id]},,0,0,0,,{placement}{opacity}{maskTags}{body.Text}"));
            }
            order++;
        }
        if (document.Layers.Any(layer => layer.Kind != LayerKind.SUBTITLE))
        {
            diagnostics.Add(new("Subtitle.Composition", "ASS 导出未包含项目中的图形或图片片段。"));
        }
        var text = result.ToString();
        AssFormatValues.CheckText(text);
        return new(text, diagnostics.DistinctBy(diagnostic => (diagnostic.SubtitleId, diagnostic.Code)).ToImmutableArray());
    }

    private static AssStyleDefinition ParseStyle(Dictionary<string, string> row, double sx, double sy, int wrapStyle, AssResolutionContext resolution,
        ImmutableArray<SubtitleFormatDiagnostic>.Builder diagnostics)
    {
        var spacing = AssFormatValues.Number(Get(row, "Spacing", "0")) * sx;
        if (!double.IsFinite(spacing) || spacing is < -4096 or > 4096)
        {
            diagnostics.Add(new("Ass.LetterSpacingRange", $"ASS 样式 {Required(row, "Name")} 的字距重采样后超出原生范围，已忽略该字距并保留其他样式。"));
            spacing = 0;
        }
        var style = new SubtitleStyle
        {
            FontFamily = Required(row, "Fontname"), FontSize = AssFormatValues.Number(Required(row, "Fontsize")) * sy,
            LetterSpacing = spacing, WrapMode = wrapStyle == 2 ? SubtitleWrapMode.NO_WRAP : SubtitleWrapMode.NATURAL,
            Fill = AssFormatValues.Color(Required(row, "PrimaryColour")), Stroke = AssFormatValues.Color(Get(row, "OutlineColour", "&H00000000")),
            ShadowColor = AssFormatValues.Color(Get(row, "BackColour", "&H00000000")),
            Bold = AssFormatValues.Integer(Get(row, "Bold", "0")) != 0, Italic = AssFormatValues.Integer(Get(row, "Italic", "0")) != 0,
            Underline = AssFormatValues.Integer(Get(row, "Underline", "0")) != 0, Strikethrough = AssFormatValues.Integer(Get(row, "StrikeOut", "0")) != 0,
            StrokeWidth = resolution.Stroke(AssFormatValues.Number(Get(row, "Outline", "0"))),
            ShadowOffset = resolution.Shadow(AssFormatValues.Number(Get(row, "Shadow", "0"))),
            ShadowBlur = 0, Alignment = AssFormatValues.Alignment(AssFormatValues.Integer(Get(row, "Alignment", "2"))),
            Margins = new(
                AssFormatValues.Number(Get(row, "MarginL", Get(row, "MarginV", "0"))) * sx,
                AssFormatValues.Number(Get(row, "MarginR", Get(row, "MarginV", "0"))) * sx,
                AssFormatValues.Number(Get(row, "MarginV", "0")) * sy)
        };
        if (resolution.ApproximatesStroke && style.StrokeWidth > 0)
        {
            diagnostics.Add(new("Ass.BorderResampling", "ASS 描边的横纵重采样比例不同，已按几何平均比例近似转换为原生单一描边宽度。"));
        }
        ProjectValidator.ValidateSubtitleStyle(style);
        return new(Required(row, "Name"), style, AssFormatValues.Color(Get(row, "SecondaryColour", "&H000000FF")))
        {
            Scale = new(AssFormatValues.Number(Get(row, "ScaleX", "100")) / 100, AssFormatValues.Number(Get(row, "ScaleY", "100")) / 100),
            Rotation = AssFormatValues.Number(Get(row, "Angle", "0"))
        };
    }

    private static string[] ParseFormat(string value)
    {
        var format = value.Split(',', StringSplitOptions.TrimEntries);
        if (format.Length == 0 || format.Any(string.IsNullOrEmpty) || format.Distinct(StringComparer.OrdinalIgnoreCase).Count() != format.Length)
        {
            throw new InvalidDataException("ASS Format 含重复或空字段。");
        }
        return format;
    }

    private static Dictionary<string, string> ParseFields(string value, string[] format)
    {
        var values = value.Split(',', format.Length);
        if (values.Length != format.Length)
        {
            throw new InvalidDataException("ASS 字段数量不匹配。");
        }
        return format.Select((field, index) => (field, Value: field.Equals("Text", StringComparison.OrdinalIgnoreCase) ? values[index] : values[index].Trim()))
            .ToDictionary(pair => pair.field, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string Required(Dictionary<string, string> fields, string key) => fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException("ASS 缺少 " + key + " 字段。");
    private static string Get(Dictionary<string, string> fields, string key, string fallback) => fields.TryGetValue(key, out var value) ? value : fallback;
}

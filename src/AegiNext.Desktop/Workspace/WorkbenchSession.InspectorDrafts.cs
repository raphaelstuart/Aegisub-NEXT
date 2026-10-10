using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private ProjectDocument PrepareInspectorDrafts(ProjectDocument document, bool preview)
    {
        var prepared = document;
        var selected = SelectedLayer;
        if (stylesDirty && selected is not null)
        {
            var vm = ViewModel.Styles;
            ViewModel.InvalidPanelId = "styles";
            ViewModel.InvalidFieldKey = "FontCombo";
            if (selected.SubtitleId is { } id)
            {
                var line = prepared.Subtitles.Single(value => value.Id == id);
                var selection = preview ? vm.CurrentFont : Fonts.Resolve(vm.FontDraft, line.Style);
                var family = selection.FamilyName;
                var parsesFontDraft = !preview && (family != vm.FontFamily || selection.Variant != vm.FontVariant);
                var fontSize = ReadNumber(vm.FontSizeText, line.Style.FontSize, "Size", "FontSizeInput");
                var lineHeight = ReadNumber(vm.LineHeightText, line.Style.LineHeight, "LineHeight", "LineHeightInput");
                var strokeWidth = PrepareStrokeWidth(ref prepared, selected, line.Style.StrokeWidth, vm.StrokeWidthText);
                var letterSpacing = PrepareStyleAnimationNumber(ref prepared, selected, AnimationProperty.LETTER_SPACING,
                    line.Style.LetterSpacing, vm.LetterSpacingText, "LetterSpacing");
                var fillBlur = PrepareStyleAnimationNumber(ref prepared, selected, AnimationProperty.FILL_BLUR,
                    line.Style.FillBlur, vm.FillBlurText, "FillBlur");
                var strokeBlur = PrepareStyleAnimationNumber(ref prepared, selected, AnimationProperty.STROKE_BLUR,
                    line.Style.StrokeBlur, vm.StrokeBlurText, "StrokeBlur");
                var fill = PrepareColor(ref prepared, selected, ReadColorDraft(vm.FillDraft, "FillPicker"), line.Style.Fill, false);
                var stroke = PrepareColor(ref prepared, selected, ReadColorDraft(vm.StrokeDraft, "StrokePicker"), line.Style.Stroke, true);
                var shadowX = ReadNumber(vm.ShadowXText, line.Style.ShadowOffset.X, "ShadowX", "ShadowXInput");
                var shadowY = ReadNumber(vm.ShadowYText, line.Style.ShadowOffset.Y, "ShadowY", "ShadowYInput");
                var shadowBlur = ReadNumber(vm.ShadowBlurText, line.Style.ShadowBlur, "ShadowBlur", "ShadowBlurInput");
                var shadowColor = ReadColorDraft(vm.ShadowDraft, "ShadowPicker");
                if (fontSize <= 0 || strokeWidth < 0 || string.IsNullOrWhiteSpace(vm.FontFamily))
                {
                    throw new InvalidDataException(Localization.Get("Workbench.Font") + ": " + Localization.Get("Workbench.Size"));
                }

                var fontChanged = family != line.Style.FontFamily || selection.Variant != line.Style.FontVariant || vm.FontSelectionCommitted;
                if (vm.Margins.Validate() is { } marginKey)
                {
                    ViewModel.InvalidFieldKey = marginKey;
                    throw new InvalidDataException(Localization.Get("Workbench.Margins"));
                }
                if (vm.Position.Validate() is { } positionKey)
                {
                    ViewModel.InvalidFieldKey = positionKey;
                    throw new InvalidDataException(Localization.Get("Workbench.ExplicitPosition"));
                }
                var style = line.Style with
                {
                    FontFamily = family,
                    FontVariant = selection.Variant,
                    FontAssetId = fontChanged ? null : line.Style.FontAssetId,
                    FontSize = fontSize,
                    LineHeight = lineHeight,
                    LetterSpacing = letterSpacing,
                    FillBlur = fillBlur,
                    StrokeBlur = strokeBlur,
                    WrapMode = (SubtitleWrapMode)vm.WrapMode,
                    Margins = vm.Margins.CreateMargins(),
                    StrokeWidth = strokeWidth,
                    Fill = fill,
                    Stroke = stroke,
                    ShadowOffset = new(shadowX, shadowY),
                    ShadowBlur = shadowBlur,
                    ShadowColor = shadowColor,
                    Bold = parsesFontDraft && selection.Variant is { } selectedVariant ? selectedVariant.Weight >= 700 : vm.Bold == true,
                    Italic = parsesFontDraft && selection.Variant is { } selectedItalicVariant ? selectedItalicVariant.Italic : vm.Italic == true,
                    Alignment = (TextAlignment)vm.Alignment,
                    TextAlign = vm.AlignmentSelectionCommitted ? null : line.Style.TextAlign,
                    Position = vm.Position.CreatePosition()
                };
                prepared = WorkspaceDraftOperations.UpdateSubtitle(prepared, id, cue => cue with { Style = style });
            }
            else
            {
                var fill = PrepareColor(ref prepared, selected, ReadColorDraft(vm.FillDraft, "FillPicker"), selected.Fill, false);
                var stroke = PrepareColor(ref prepared, selected, ReadColorDraft(vm.StrokeDraft, "StrokePicker"), selected.Stroke, true);
                var width = PrepareStrokeWidth(ref prepared, selected, selected.StrokeWidth, vm.StrokeWidthText);
                prepared = WorkspaceDraftOperations.UpdateLayer(prepared, selected.Id, layer => layer with
                {
                    Fill = fill, Stroke = stroke, StrokeWidth = width
                });
            }
        }

        if (effectsDirty && selected is not null)
        {
            var vm = ViewModel.Effects;
            ViewModel.InvalidPanelId = "effects";
            var keyframeChanged = changedEffectFields.Overlaps(["KeyframeValueText", "KeyframeValue", "KeyframeValueY", "KeyframeValueYText", "KeyframeColorDraft"]);
            var keyframeValue = ReadKeyframeDraft(vm, keyframeChanged);
            ViewModel.InvalidFieldKey = "LayerStartInput";
            var start = vm.LayerStart == TimelineTimeText.Format(selected.Start) ? selected.Start : TimelineTimeText.Parse(vm.LayerStart);
            ViewModel.InvalidFieldKey = "LayerEndInput";
            var end = vm.LayerEnd == TimelineTimeText.Format(selected.End) ? selected.End : TimelineTimeText.Parse(vm.LayerEnd);
            if (selected.SubtitleId is { } id && (start != selected.Start || end != selected.End))
            {
                prepared = WorkspaceDraftOperations.UpdateSubtitle(prepared, id, line => line with { Start = start, End = end });
            }

            var originalPlacement = ResolvePlacement(editor.Snapshot, selected);
            var preparedLayer = prepared.Layers.Single(value => value.Id == selected.Id);
            var preparedPlacement = ResolvePlacement(prepared, preparedLayer);
            var target = AnimationTarget!;
            var positionX = InspectorVector(selected, AnimationProperty.POSITION, selected.Transform.Position).X;
            var positionY = InspectorVector(selected, AnimationProperty.POSITION, selected.Transform.Position).Y;
            if (InspectorPosition(selected, originalPlacement) is { } displayedPosition)
            {
                var displayedX = displayedPosition.X;
                var displayedY = displayedPosition.Y;
                var requestedX = ReadEffectNumber(vm.PositionXText, displayedX, "PositionX", "PositionXInput", "PositionXText");
                var requestedY = ReadEffectNumber(vm.PositionYText, displayedY, "PositionY", "PositionYInput", "PositionYText");
                if (requestedX != displayedX || requestedY != displayedY)
                {
                    if (SceneEditing.Target.TextRangeId is not null)
                    {
                        positionX = requestedX;
                        positionY = requestedY;
                    }
                    else
                    {
                        if (preparedPlacement.BasePosition is not { } preparedBase)
                        {
                            throw new InvalidDataException(Localization.Get("Workbench.SubtitlePositionUnavailable"), preparedPlacement.Error);
                        }
                        positionX = requestedX == displayedX ? positionX : requestedX - preparedBase.X;
                        positionY = requestedY == displayedY ? positionY : requestedY - preparedBase.Y;
                    }
                }
            }
            else if (!string.IsNullOrEmpty(vm.PositionXText) || !string.IsNullOrEmpty(vm.PositionYText))
            {
                throw new InvalidDataException(Localization.Get("Workbench.SubtitlePositionUnavailable"), originalPlacement.Error);
            }

            prepared = WorkspaceDraftOperations.UpdateLayer(prepared, selected.Id, layer => layer with
            {
                Start = start,
                End = end,
                AnimationOffset = layer.AnimationOffset + start - layer.Start,
                Shape = layer.Shape is { } shape ? shape with
                {
                    Width = ReadNumber(vm.LayerWidthText, shape.Width, "Size", "LayerWidthInput"), Height = ReadNumber(vm.LayerHeightText, shape.Height, "Size", "LayerHeightInput")
                } : null,
                Image = layer.Image is { } image ? image with
                {
                    Width = ReadNumber(vm.LayerWidthText, image.Width, "Size", "LayerWidthInput"), Height = ReadNumber(vm.LayerHeightText, image.Height, "Size", "LayerHeightInput")
                } : null,
                Blend = (BlendMode)vm.Blend,
                MotionPath = layer.MotionPath is { } path ? path with { OrientToPath = vm.OrientPath == true } : null
            });
            foreach (var (property, text, fallback, label, field) in new[]
            {
                (AnimationProperty.ROTATION, vm.RotationText, selected.Transform.Rotation, "Rotation", "RotationInput"),
                (AnimationProperty.OPACITY, vm.OpacityText, selected.Opacity, "Opacity", "OpacityInput"),
                (AnimationProperty.BLUR, vm.BlurText, selected.Blur, "Blur", "BlurInput")
            })
            {
                var original = InspectorValue(selected, property, fallback);
                var requested = ReadEffectNumber(text, original, label, field, label + "Text");
                if (requested != original)
                {
                    prepared = AnimationEditOperations.SetValue(prepared, target, property, requested);
                }
            }
            var originalPosition = InspectorVector(selected, AnimationProperty.POSITION, selected.Transform.Position);
            var requestedPosition = new ScenePoint(positionX, positionY);
            if (requestedPosition != originalPosition)
            {
                prepared = AnimationEditOperations.SetValue(prepared, target, AnimationProperty.POSITION, requestedPosition);
            }
            var originalScale = InspectorVector(selected, AnimationProperty.SCALE, selected.Transform.Scale);
            var requestedScale = new ScenePoint(
                ReadEffectNumber(vm.ScaleXText, originalScale.X, "ScaleX", "ScaleXInput", "ScaleXText"),
                ReadEffectNumber(vm.ScaleYText, originalScale.Y, "ScaleY", "ScaleYInput", "ScaleYText"));
            if (requestedScale != originalScale)
            {
                prepared = AnimationEditOperations.SetValue(prepared, target, AnimationProperty.SCALE, requestedScale);
            }
            if (keyframeChanged)
            {
                prepared = AnimationEditOperations.SetValue(prepared, target, target.Target ?? SceneEditing.Target, keyframeValue);
            }
            if (target.IsKeyframe && (keyframeChanged || changedEffectFields.Overlaps(["Interpolation", "PowerExponent"])))
            {
                var property = target.Target ?? SceneEditing.Target;
                var frame = selected.Tracks.FirstOrDefault(track => track.Target == property)?.Keyframes.FirstOrDefault(key => key.Time == target.LocalTime) ?? new Keyframe(target.LocalTime, keyframeValue);
                var preservesCurve = vm.Interpolation == (int)frame.Interpolation && vm.ReadPowerExponent() == frame.Exponent;
                prepared = WorkspaceDraftOperations.SetKeyframe(prepared, selected.Id, property, frame with
                {
                    Value = keyframeChanged ? keyframeValue : frame.Value,
                    Interpolation = (KeyframeInterpolation)vm.Interpolation,
                    Exponent = vm.ReadPowerExponent(),
                    Reverse = preservesCurve && frame.Reverse,
                    CurveStart = preservesCurve ? frame.CurveStart : 0,
                    CurveEnd = preservesCurve ? frame.CurveEnd : 1,
                    ComponentCurves = preservesCurve ? frame.ComponentCurves : []
                });
            }
        }

        return prepared;
    }
}

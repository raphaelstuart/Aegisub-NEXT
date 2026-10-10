using System.Collections.Immutable;
using System.ComponentModel;
using AegiNext.Application;
using AegiNext.Core.Projects;
using AegiNext.Core.Timing;
using AegiNext.Desktop.Controls;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;

namespace AegiNext.Desktop.Workspace;

internal sealed partial class WorkbenchSession
{
    private static readonly HashSet<string> styleDraftProperties =
    [
        "FontFamily", "FontVariant", "FontSelectionCommitted", "FontDraft", "FontSize", "FontSizeText", "StrokeWidth", "StrokeWidthText", "Fill", "Stroke", "FillDraft", "StrokeDraft", "Bold", "Italic", "Alignment", "AlignmentSelectionCommitted", "Position",
        "ShadowX", "ShadowXText", "ShadowY", "ShadowYText", "ShadowBlur", "ShadowBlurText", "ShadowDraft", "LineHeight", "LineHeightText", "Margins", "LetterSpacing", "LetterSpacingText", "FillBlur", "FillBlurText", "StrokeBlur", "StrokeBlurText", "WrapMode"
    ];
    private static readonly HashSet<string> effectDraftProperties =
    [
        "LayerStart", "LayerEnd", "LayerWidth", "LayerWidthText", "LayerHeight", "LayerHeightText", "PositionX", "PositionXText", "PositionY", "PositionYText", "ScaleX", "ScaleXText", "ScaleY", "ScaleYText",
        "Rotation", "RotationText", "Opacity", "OpacityText", "Blur", "BlurText", "Blend", "OrientPath", "KeyframeValue", "KeyframeValueText", "KeyframeValueY", "KeyframeValueYText", "KeyframeColorDraft", "Interpolation", "PowerExponent"
    ];

    private readonly HashSet<string> changedEffectFields = [];

    internal bool TryCommitDrafts(bool focusInvalid = true)
    {
        return TryCommitDraftsCore(focusInvalid, false);
    }

    internal bool TryCommitDraftsForClose(AegiNext.Application.Tasks.AegiTaskScopeCloseLease closeLease)
    {
        ArgumentNullException.ThrowIfNull(closeLease);
        if (!closing || closeLease.ScopeId != TaskScope || applicationContext.Tasks.GetSnapshots().Any(task =>
            task.ScopeId == TaskScope && !task.IsFinished))
        {
            throw new InvalidOperationException("Closing drafts require a drained project scope.");
        }
        return TryCommitDraftsCore(true, true);
    }

    private bool TryCommitDraftsCore(bool focusInvalid, bool allowClosing)
    {
        NumericGestureCancellationRequested?.Invoke(this, EventArgs.Empty);
        if (IsUpdating || closing && !allowClosing)
        {
            return !closing || allowClosing;
        }

        var document = editor.Snapshot;
        var prepared = document;
        if (!Details.TryPrepare(document, out prepared))
        {
            ViewModel.InvalidPanelId = "subtitleDetails";
            ViewModel.InvalidFieldKey = Details.InvalidFieldKey;
            if (focusInvalid)
            {
                ViewModel.FocusDraftError();
            }
            return false;
        }
        Guid? invalidRow = null;
        try
        {
            var changes = new Dictionary<Guid, SubtitleLine>();
            foreach (var row in ViewModel.Subtitles.Rows.Where(value => value.IsDirty))
            {
                invalidRow = row.Id;
                ViewModel.InvalidPanelId = "subtitles";
                ViewModel.InvalidFieldKey = row.StartText != TimelineTimeText.Format(row.Original.Start) ? "StartText" : row.EndText != TimelineTimeText.Format(row.Original.End) ? "EndText" : "Text";
                var line = prepared.Subtitles.FirstOrDefault(value => value.Id == row.Id);
                if (line is not null)
                {
                    changes.Add(row.Id, row.CreateEditedLine(line, prepared));
                }
            }

            invalidRow = null;
            foreach (var change in changes.Values)
            {
                prepared = WorkspaceDraftOperations.UpdateSubtitle(prepared, change.Id, _ => change);
            }

            prepared = PrepareInspectorDrafts(prepared, false);
            prepared = MaskEditing.Prepare(prepared);
            prepared = ViewModel.Effects.PrepareOperationDraft(prepared);
            prepared = PropertyEditing.Prepare(prepared);

            if (!ReferenceEquals(prepared, document))
            {
                ProjectValidator.Validate(prepared);
            }
            ViewModel.InvalidPanelId = "export";
            try
            {
                ViewModel.Export.AcceptSettings(ViewModel.Export.CaptureSettings());
            }
            catch (ExportSettingsValidationException error)
            {
                ViewModel.InvalidFieldKey = error.FieldKey;
                throw new InvalidDataException(Localization.Get(error.LocalizationKey), error);
            }
            var originalStylesDirty = stylesDirty;
            var originalEffectsDirty = effectsDirty;
            stylesDirty = false;
            effectsDirty = false;
            var originalFields = changedEffectFields.ToArray();
            var originalTarget = SceneEditing.DraftTarget;
            SceneEditing.DraftTarget = null;
            changedEffectFields.Clear();
            ClearInspectorPreview();
            MaskEditing.AcceptDrafts();
            ViewModel.Effects.AcceptOperationDraft();
            PropertyEditing.Accept();
            try
            {
                if (prepared != document)
                {
                    prepared = OverlayTimingPreview(prepared);
                    ProjectValidator.Validate(prepared);
                    InvalidateTimingSession();
                    editor.Apply("Commit workspace drafts", _ => prepared);
                }
                else
                {
                    RefreshCommittedDrafts(changes, originalStylesDirty, originalEffectsDirty, originalFields);
                }
            }
            catch
            {
                stylesDirty = originalStylesDirty;
                effectsDirty = originalEffectsDirty;
                changedEffectFields.UnionWith(originalFields);
                SceneEditing.DraftTarget = originalTarget;
                throw;
            }

            ViewModel.InvalidPanelId = null;
            ViewModel.InvalidFieldKey = null;
            ViewModel.Subtitles.ValidationError = null;
            ViewModel.Subtitles.InvalidRowId = null;
            ViewModel.Effects.ValidationError = null;
            ViewModel.Effects.InvalidFieldKey = null;
            ViewModel.Masks.ValidationError = null;
            ViewModel.Masks.InvalidFieldKey = null;
            lastDraftDiagnostic = null;
            return true;
        }
        catch (Exception error)
        {
            ViewModel.Subtitles.InvalidRowId = invalidRow;
            ViewModel.Subtitles.ValidationError = error.Message;
            ReportDraftError(error, focusInvalid);
            return false;
        }
    }

    private void RefreshCommittedDrafts(Dictionary<Guid, SubtitleLine> changes, bool refreshStyles, bool refreshEffects,
        string[] effectFields)
    {
        using var updateLease = BeginWorkbenchUpdate();
        try
        {
            if (changes.Count > 0)
            {
                foreach (var row in ViewModel.Subtitles.Rows)
                {
                    if (changes.TryGetValue(row.Id, out var line))
                    {
                        row.Accept(line);
                    }
                }
            }

            MaskEditing.Refresh(true);
            if (refreshStyles || refreshEffects)
            {
                RefreshInspector();
                if (refreshStyles)
                {
                    OnStylePropertyChanged(this, new(nameof(ViewModel.Styles.FontSize)));
                    OnStylePropertyChanged(this, new(nameof(ViewModel.Styles.StrokeWidth)));
                }
                if (refreshEffects)
                {
                    foreach (var field in effectFields)
                    {
                        var property = field.EndsWith("Text", StringComparison.Ordinal) ? field[..^4] : field;
                        OnEffectPropertyChanged(this, new(property));
                    }
                }
            }
            else if (ViewModel.Effects.SelectedOperation is not null)
            {
                ViewModel.Effects.RefreshTransformOperations(SelectedLayer?.Tracks.FirstOrDefault(track => track.Target == SceneEditing.Target));
            }
            RefreshEditingPreview();
        }
        finally
        {
            updateLease.Dispose();
        }
    }

    private AnimationValue ReadKeyframeDraft(Panels.Effects.EffectsPanelViewModel vm, bool changed)
    {
        var target = SceneEditing.DraftTarget?.Target ?? SceneEditing.Target;
        var property = target.Property;
        var vector = AnimationPropertyMetadata.GetValueKind(property) == AnimationValueKind.VECTOR;
        if (AnimationPropertyMetadata.GetValueKind(property) == AnimationValueKind.COLOR)
        {
            return ReadColorDraft(vm.KeyframeColorDraft, "KeyframeColorInput");
        }
        var previous = SelectedLayer?.Tracks.FirstOrDefault(track => track.Target == target)?.Keyframes
            .FirstOrDefault(frame => frame.Time == AnimationTarget?.LocalTime)?.Value;
        double Read(string text, string field)
        {
            var value = RequiredNumber(text, "Property", field);
            if (value < (double)vm.KeyframeMinimum || value > (double)vm.KeyframeMaximum)
            {
                throw new InvalidDataException(Localization.Get("Workbench.Property"));
            }
            return value;
        }
        if (!vector)
        {
            return changed ? Read(vm.KeyframeValueText, "KeyframeValueInput") : previous ?? (double)(vm.KeyframeValue ?? 0);
        }
        var value = previous?.Vector ?? new ScenePoint((double)(vm.KeyframeValue ?? 0), (double)(vm.KeyframeValueY ?? 0));
        var x = changedEffectFields.Overlaps(["KeyframeValue", "KeyframeValueText"]) ? Read(vm.KeyframeValueText, "KeyframeValueXInput") : value.X;
        var y = changedEffectFields.Overlaps(["KeyframeValueY", "KeyframeValueYText"]) ? Read(vm.KeyframeValueYText, "KeyframeValueYInput") : value.Y;
        return new ScenePoint(x, y);
    }

    private SceneColor ReadColorDraft(ColorDraft draft, string field)
    {
        if (!draft.TryCommit(out var value))
        {
            ViewModel.InvalidFieldKey = field;
            throw new InvalidDataException(draft.Error ?? Localization.Get("Workbench.Fill"));
        }
        return value;
    }

    private double ReadNumber(string text, double original, string label, string fieldKey)
    {
        var number = RequiredNumber(text, label, fieldKey);
        return (decimal)number == (decimal)original ? original : number;
    }

    private double RequiredNumber(string text, string label, string fieldKey)
    {
        ViewModel.InvalidFieldKey = fieldKey;
        if (!decimal.TryParse(text, System.Globalization.NumberStyles.Float, InterfaceCulture, out var number))
        {
            throw new InvalidDataException(Localization.Get("Workbench." + (label)));
        }
        var (minimum, maximum) = fieldKey switch
        {
            "FontSizeInput" => (0.01m, 4096m),
            "StrokeWidthInput" => ((decimal)AnimationPropertyMetadata.GetMinimum(AnimationProperty.STROKE_WIDTH),
                (decimal)AnimationPropertyMetadata.GetMaximum(AnimationProperty.STROKE_WIDTH)),
            "LineHeightInput" => (0.1m, 10m),
            "ShadowXInput" or "ShadowYInput" => (-1000000000m, 1000000000m),
            "ShadowBlurInput" or "FillBlurInput" or "StrokeBlurInput" => (0m, 512m),
            "LetterSpacingInput" => (-4096m, 4096m),
            "LayerWidthInput" or "LayerHeightInput" => (1m, 32768m),
            "PositionXInput" or "PositionYInput" => (-2000032768m, 2000032768m),
            "ScaleXInput" or "ScaleYInput" => ((decimal)AnimationPropertyMetadata.GetMinimum(AnimationProperty.SCALE),
                (decimal)AnimationPropertyMetadata.GetMaximum(AnimationProperty.SCALE)),
            "RotationInput" => ((decimal)AnimationPropertyMetadata.GetMinimum(AnimationProperty.ROTATION),
                (decimal)AnimationPropertyMetadata.GetMaximum(AnimationProperty.ROTATION)),
            "OpacityInput" => (0m, 1m),
            "BlurInput" => (0m, 128m),
            "CrfInput" => (0m, 51m),
            "AudioBitrateInput" => (32m, 512m),
            "VideoBitrateInput" => (0.1m, 200m),
            _ => (decimal.MinValue, decimal.MaxValue)
        };
        if (number < minimum || number > maximum)
        {
            throw new InvalidDataException(Localization.Get("Workbench." + (label)));
        }
        return (double)number;
    }

    internal void CommitRow(SubtitleRow row)
    {
        if (!IsProjectBusy && !IsUpdating && row.IsDirty)
        {
            TryCommitDrafts(false);
        }
    }

    internal void SetTextCaret(Guid id, int caret) => textCarets[id] = caret;

    private void OnStylePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "FontSize":
                ViewModel.Styles.FontSizeText = SynchronizeNumericText(ViewModel.Styles.FontSizeText, ViewModel.Styles.FontSize);
                break;
            case "StrokeWidth":
                ViewModel.Styles.StrokeWidthText = SynchronizeNumericText(ViewModel.Styles.StrokeWidthText, ViewModel.Styles.StrokeWidth);
                break;
        }

        if (!IsUpdating && e.PropertyName is { } name && styleDraftProperties.Contains(name))
        {
            stylesDirty = true;
            draftRevision++;
            NotifyTaskInputChanged();
            FreezeDraftTarget();
            if (name != "FontDraft")
            {
                QueueInspectorPreview();
            }
        }
    }

    private void OnEffectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case "LayerWidth":
                ViewModel.Effects.LayerWidthText = SynchronizeNumericText(ViewModel.Effects.LayerWidthText, ViewModel.Effects.LayerWidth);
                break;
            case "LayerHeight":
                ViewModel.Effects.LayerHeightText = SynchronizeNumericText(ViewModel.Effects.LayerHeightText, ViewModel.Effects.LayerHeight);
                break;
            case "PositionX":
                ViewModel.Effects.PositionXText = SynchronizeNumericText(ViewModel.Effects.PositionXText, ViewModel.Effects.PositionX);
                break;
            case "PositionY":
                ViewModel.Effects.PositionYText = SynchronizeNumericText(ViewModel.Effects.PositionYText, ViewModel.Effects.PositionY);
                break;
            case "ScaleX":
                ViewModel.Effects.ScaleXText = SynchronizeNumericText(ViewModel.Effects.ScaleXText, ViewModel.Effects.ScaleX);
                break;
            case "ScaleY":
                ViewModel.Effects.ScaleYText = SynchronizeNumericText(ViewModel.Effects.ScaleYText, ViewModel.Effects.ScaleY);
                break;
            case "Rotation":
                ViewModel.Effects.RotationText = SynchronizeNumericText(ViewModel.Effects.RotationText, ViewModel.Effects.Rotation);
                break;
            case "Opacity":
                ViewModel.Effects.OpacityText = SynchronizeNumericText(ViewModel.Effects.OpacityText, ViewModel.Effects.Opacity);
                break;
            case "Blur":
                ViewModel.Effects.BlurText = SynchronizeNumericText(ViewModel.Effects.BlurText, ViewModel.Effects.Blur);
                break;
            case "KeyframeValueY":
                ViewModel.Effects.KeyframeValueYText = SynchronizeNumericText(ViewModel.Effects.KeyframeValueYText, ViewModel.Effects.KeyframeValueY);
                break;
            case "KeyframeValue":
                ViewModel.Effects.KeyframeValueText = SynchronizeNumericText(ViewModel.Effects.KeyframeValueText, ViewModel.Effects.KeyframeValue);
                break;
        }

        if (IsUpdating || e.PropertyName is null)
        {
            return;
        }

        if (effectDraftProperties.Contains(e.PropertyName))
        {
            effectsDirty = true;
            draftRevision++;
            NotifyTaskInputChanged();
            FreezeDraftTarget();
            changedEffectFields.Add(e.PropertyName);
            QueueInspectorPreview();
        }
        else if (e.PropertyName == "EditMode")
        {
            RefreshEditingPreview();
        }
        else if (e.PropertyName == "Property")
        {
            using var updateLease = BeginWorkbenchUpdate();
            try
            {
                ViewModel.Timeline.EffectTarget = SceneEditing.Target;
                RefreshKeyframeInspector();
                RefreshInspector();
            }
            finally
            {
                updateLease.Dispose();
            }
        }
    }

    internal void RefreshDocument(bool preserveDrafts = false)
    {
        using var updateLease = BeginWorkbenchUpdate();
        try
        {
            var document = editor.Snapshot;
            RefreshSubtitleTracks();
            Volatile.Write(ref previewState, CreatePreviewState());
            var oldRows = ViewModel.Subtitles.Rows.ToDictionary(value => value.Id);
            var rows = document.Subtitles.Select((line, index) =>
            {
                if (!oldRows.TryGetValue(line.Id, out var row) || row.Number != index + 1)
                {
                    return new SubtitleRow(line, index + 1);
                }

                if (!row.IsDirty || line != row.Original)
                {
                    row.Accept(line);
                }

                return row;
            }).ToArray();
            if (!ViewModel.Subtitles.Rows.SequenceEqual(rows))
            {
                ViewModel.Subtitles.Rows = rows;
            }

            if (!document.Subtitles.Any(value => value.Id == SelectedCueId))
            {
                SelectedCueId = null;
            }
            ViewModel.Subtitles.RefreshColorTags();
            var layers = document.Layers;
            if (!layers.Any(value => value.Id == SelectedLayerId))
            {
                SelectedLayerId = SelectedCueId is { } cueId ? document.Layers.FirstOrDefault(layer => layer.SubtitleId == cueId)?.Id : null;
            }

            if (SelectedKeyTime is { } selectedTime && SelectedLayer?.Tracks.Any(track => track.Keyframes.Any(frame => frame.Time == selectedTime)) != true)
            {
                SelectedKeyTime = null;
            }
            SyncCurrentTrackForSelection();
            RefreshSubtitleSelection();
            ViewModel.Subtitles.SelectedRow = rows.FirstOrDefault(value => value.Id == SelectedCueId);
            RefreshTitle();
            ViewModel.Timeline.Document = document;
            ViewModel.Timeline.SelectedCueId = SelectedCueId;
            ViewModel.Timeline.SelectedLayer = SelectedLayer;
            var selectedIds = ViewModel.Effects.SelectedIds.Where(id => layers.Any(layer => layer.Id == id)).ToArray();
            if (SelectedLayerId is { } primary && !selectedIds.Contains(primary))
            {
                selectedIds = [primary];
            }
            ViewModel.Effects.SelectedIds = selectedIds;
            ViewModel.Timeline.SelectedLayerIds = selectedIds;
            ViewModel.Effects.Document = document;
            ViewModel.Effects.SelectedLayer = SelectedLayer;
            ViewModel.Effects.RefreshPropertyGrid();
            MaskEditing.Refresh(!preserveDrafts);
            RefreshInspector();
            ViewModel.Styles.CanApplyPreset = SelectedCue is not null && !IsProjectBusy && !closing && ViewModel.Styles.SelectedPreset is not null;
            Tick();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            updateLease.Dispose();
        }
    }

    private void RefreshInspector()
    {
        var layer = SelectedLayer;
        var cue = layer?.SubtitleId is { } id ? editor.Snapshot.Subtitles.First(value => value.Id == id) : null;
        var style = cue?.Style ?? new();
        var placement = ResolvePlacement(editor.Snapshot, layer);
        var vm = ViewModel.Styles;
        vm.LoadCanvasSize(editor.Snapshot.Width, editor.Snapshot.Height);
        vm.HasCue = cue is not null;
        vm.RefreshAppearanceEditing();
        if (!stylesDirty)
        {
            vm.LoadFont(style);
            vm.FontSize = (decimal)style.FontSize;
            vm.StrokeWidth = (decimal)(layer is null ? 0 : InspectorValue(layer, AnimationProperty.STROKE_WIDTH, cue is null ? layer.StrokeWidth : style.StrokeWidth));
            vm.FillDraft.Load(layer is null ? SceneColor.White : InspectorColor(layer, cue is null ? layer.Fill : style.Fill, false));
            vm.StrokeDraft.Load(layer is null ? SceneColor.Black : InspectorColor(layer, cue is null ? layer.Stroke : style.Stroke, true));
            vm.LoadStyleNumbers(layer is null ? style : style with
            {
                LetterSpacing = InspectorValue(layer, AnimationProperty.LETTER_SPACING, style.LetterSpacing),
                FillBlur = InspectorValue(layer, AnimationProperty.FILL_BLUR, style.FillBlur),
                StrokeBlur = InspectorValue(layer, AnimationProperty.STROKE_BLUR, style.StrokeBlur)
            }, InterfaceCulture);
            vm.WrapMode = (int)style.WrapMode;
            vm.ShadowDraft.Load(style.ShadowColor);
            vm.Bold = style.Bold;
            vm.Italic = style.Italic;
            vm.LoadAlignment(style.Alignment);
            vm.Position.Load(style, placement.Position, placement.BasePosition is not null,
                PreparePositionGeometry(placement.Geometry, layer));
        }

        var effects = ViewModel.Effects;
        if (!effectsDirty)
        {
            var transform = layer?.Transform ?? new();
            if (layer is not null)
            {
                transform = transform with
                {
                    Position = InspectorVector(layer, AnimationProperty.POSITION, transform.Position),
                    Scale = InspectorVector(layer, AnimationProperty.SCALE, transform.Scale),
                    Rotation = InspectorValue(layer, AnimationProperty.ROTATION, transform.Rotation)
                };
            }
            var position = InspectorPosition(layer, placement);
            effects.CanEditPosition = position is not null;
            effects.PositionX = position is { } displayedX ? (decimal)displayedX.X : null;
            effects.PositionY = position is { } displayedY ? (decimal)displayedY.Y : null;
            effects.ScaleX = (decimal)transform.ScaleX;
            effects.ScaleY = (decimal)transform.ScaleY;
            effects.Rotation = (decimal)transform.Rotation;
            effects.Opacity = (decimal)(layer is null ? 1 : InspectorValue(layer, AnimationProperty.OPACITY, layer.Opacity));
            effects.Blur = (decimal)(layer is null ? 0 : InspectorValue(layer, AnimationProperty.BLUR, layer.Blur));
            effects.LayerWidth = (decimal)(layer?.Shape?.Width ?? layer?.Image?.Width ?? 300);
            effects.LayerHeight = (decimal)(layer?.Shape?.Height ?? layer?.Image?.Height ?? 180);
            effects.CanResizeLayer = layer?.Shape is not null || layer?.Image is not null;
            effects.LayerStart = layer is null ? string.Empty : TimelineTimeText.Format(layer.Start);
            effects.LayerEnd = layer is null ? string.Empty : TimelineTimeText.Format(layer.End);
            effects.Blend = (int)(layer?.Blend ?? BlendMode.NORMAL);
            effects.OrientPath = layer?.MotionPath?.OrientToPath ?? false;
            RefreshKeyframeInspector();
        }

        effects.RefreshPropertyGrid();
        RefreshEditingTargetLabel();
        effectScripts?.RefreshChoices();
    }

    private SubtitlePositionGeometry? PreparePositionGeometry(SubtitlePositionGeometry? geometry, ProjectLayer? layer)
    {
        if (geometry is null || layer is null)
        {
            return geometry;
        }
        return geometry with
        {
            Transform = layer.Transform with
            {
                Scale = InspectorVector(layer, AnimationProperty.SCALE, layer.Transform.Scale),
                Rotation = InspectorValue(layer, AnimationProperty.ROTATION, layer.Transform.Rotation)
            }
        };
    }

    private Rendering.LayerPlacementResolution ResolvePlacement(ProjectDocument document, ProjectLayer? layer)
    {
        var result = layerPlacement.Resolve(document, projectDirectory, layer, AnimationTarget?.LocalTime);
        if (result.Error is { } error)
        {
            placementDiagnostic = new InvalidDataException($"{Localization.Get("Workbench.SubtitlePositionUnavailable")}: {error.Message}", error);
            SetDiagnosticError("Subtitle placement", placementDiagnostic);
            ShowError(placementDiagnostic, false);
        }
        else if (placementDiagnostic is not null)
        {
            var source = placementDiagnostic.InnerException;
            if (ReferenceEquals(LastError, placementDiagnostic) ||
                source is not null && LastError is { } previous && previous.GetType() == source.GetType() && previous.Message == source.Message)
            {
                LastError = null;
                ViewModel.Error = null;
            }

            placementDiagnostic = null;
            SetDiagnosticError("Subtitle placement", null);
        }

        return result;
    }

    private void RestorePlacementDiagnostic()
    {
        if (LastError is null && placementDiagnostic is { } error)
        {
            ShowError(error, false);
        }
    }

    internal void SelectCue(Guid id)
    {
        if (IsUpdating || IsProjectBusy || id == SelectedCueId && SelectedLayer?.SubtitleId == id && SelectedSubtitleIds.Count <= 1)
        {
            return;
        }

        if (!TryCommitDrafts())
        {
            RefreshDocument();
            return;
        }

        ViewModel.CancelGestures();
        InvalidateTimingSession();
        var revealed = RevealSubtitleColorTagTargets([id]);
        SynchronizeCueSelection(id);
        RefreshDocument();
        if (revealed)
        {
            SubtitleScrollRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SynchronizeCueSelection(Guid id)
    {
        ResetSubtitleSelection(id);
        SelectedCueId = id;
        ViewModel.Effects.SelectedIds = [];
        SelectedLayerId = editor.Snapshot.Layers.FirstOrDefault(layer => layer.SubtitleId == id)?.Id;
        SelectedKeyTime = null;
        ViewModel.Effects.EditMode = CanvasEditMode.POSITION;
    }

    internal void SelectLayer(Guid id, Guid[] selectedIds)
    {
        if (IsUpdating || IsProjectBusy)
        {
            return;
        }

        if (id != SelectedLayerId && !TryCommitDrafts())
        {
            RefreshDocument();
            return;
        }

        ViewModel.CancelGestures();
        if (id != SelectedLayerId || !ViewModel.Effects.SelectedIds.ToHashSet().SetEquals(selectedIds))
        {
            InvalidateTimingSession();
        }
        SelectedLayerId = id;
        var revealed = RevealSubtitleColorTagTargets(editor.Snapshot.Layers.Where(layer => selectedIds.Contains(layer.Id) || layer.Id == id)
            .Select(layer => layer.SubtitleId).OfType<Guid>());
        ViewModel.Effects.SelectedIds = selectedIds;
        SelectedKeyTime = null;
        ViewModel.Effects.EditMode = CanvasEditMode.POSITION;
        if (SelectedLayer?.SubtitleId is { } cueId)
        {
            SelectedCueId = cueId;
        }

        SynchronizeSubtitleSelectionFromLayers();
        RefreshDocument();
        if (revealed)
        {
            SubtitleScrollRequested?.Invoke(this, EventArgs.Empty);
        }
    }

}

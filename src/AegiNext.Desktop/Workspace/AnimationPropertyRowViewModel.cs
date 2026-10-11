using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using AegiNext.Core.Projects;
using AegiNext.Desktop.Editing;
using AegiNext.Desktop.I18n;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Workspace;

internal sealed class AnimationPropertyRowViewModel : ObservableObject
{
    private readonly WorkbenchSession session;
    private bool loading;
    private string originalX = string.Empty;
    private string originalY = string.Empty;
    private AnimationEditTarget? draftTarget;
    private ProjectDocument? draftSource;
    private bool isAnimated;
    private bool isEnabled;
    private bool isMixed;
    private bool canAddKeyframe;
    private string? error;
    private string? invalidFieldKey;
    private string activePanel = "effects";
    private string editingPanel = "effects";
    private string xEditingPanel = "effects";
    private string yEditingPanel = "effects";
    private string colorEditingPanel = "effects";

    internal AnimationPropertyRowViewModel(WorkbenchSession session, Guid layerId, AnimationTrackTarget target)
    {
        this.session = session;
        LayerId = layerId;
        Target = target;
        X.PropertyChanged += OnNumberChanged;
        Y.PropertyChanged += OnNumberChanged;
        Color.Changed += OnColorChanged;
        Color.Committed += (_, _) => session.TryCommitDrafts(false);
        SelectCommand = new RelayCommand(() => session.SelectAnimationProperty(LayerId, Target));
        ToggleAnimationCommand = new AsyncRelayCommand(() => session.SetAnimationPropertyEnabledAsync(LayerId, Target, !IsAnimated));
        AddKeyframeCommand = new AsyncRelayCommand(() => session.AddAnimationPropertyKeyframeAsync(LayerId, Target));
        DetailsCommand = new RelayCommand(() => session.OpenAnimationPropertyDetails(LayerId, Target));
        ResetCommand = new AsyncRelayCommand(() => session.ResetAnimationPropertyAsync(LayerId, Target));
    }

    internal Guid LayerId { get; }
    internal AnimationTrackTarget Target { get; }
    internal bool HasDraft => X.RawText != originalX || IsVector && Y.RawText != originalY || IsColor && Color.IsDirty;
    public string Name => AnimationPropertyLocalization.Get(Target.Property);
    public string FieldKey => $"EffectProperty_{LayerId:N}_{Target.NodeId:N}_{Target.TextRangeId:N}_{Target.State}_{Target.Property}";
    public string XFieldKey => FieldKey + "_X";
    public string YFieldKey => FieldKey + "_Y";
    public NumericValueDraft X { get; } = new();
    public NumericValueDraft Y { get; } = new();
    public ColorDraft Color { get; } = new();
    public bool IsScalar => AnimationPropertyMetadata.GetValueKind(Target.Property) == AnimationValueKind.SCALAR;
    public bool IsVector => AnimationPropertyMetadata.GetValueKind(Target.Property) == AnimationValueKind.VECTOR;
    public bool IsColor => AnimationPropertyMetadata.GetValueKind(Target.Property) == AnimationValueKind.COLOR;
    public decimal Minimum => (decimal)AnimationPropertyMetadata.GetMinimum(Target.Property);
    public decimal Maximum => (decimal)AnimationPropertyMetadata.GetMaximum(Target.Property);
    public decimal Increment => Target.Property switch
    {
        AnimationProperty.SCALE or AnimationProperty.MASK_SCALE => 0.01m,
        AnimationProperty.LETTER_SPACING or AnimationProperty.STROKE_WIDTH or AnimationProperty.FILL_BLUR or
            AnimationProperty.STROKE_BLUR or AnimationProperty.SHADOW_BLUR => 0.5m,
        _ => 1m
    };
    public bool IsAnimated { get => isAnimated; private set => SetProperty(ref isAnimated, value); }
    public bool IsEnabled { get => isEnabled; private set => SetProperty(ref isEnabled, value); }
    public bool CanAddKeyframe { get => canAddKeyframe; private set => SetProperty(ref canAddKeyframe, value); }
    public bool IsOrdered => !IsEnabled;
    public ICommand DetailsCommand { get; }
    public string DetailsHint { get; private set; } = Localization.Get("Workbench.EditAnimationDetails");
    public bool CanReset => IsAnimated && IsEnabled;
    public bool CanToggleAnimation => IsAnimated || CanAddKeyframe;
    public bool IsMixed { get => isMixed; private set => SetProperty(ref isMixed, value); }
    public string? EditingHint => IsMixed ? Localization.Get("Workbench.MixedPropertyValue") : null;
    public string? Error { get => error; private set => SetProperty(ref error, value); }
    public string? InvalidFieldKey { get => invalidFieldKey; private set => SetProperty(ref invalidFieldKey, value); }
    public string AnimationHint => Localization.Get(IsAnimated ? "Workbench.DisablePropertyAnimation" : "Workbench.EnablePropertyAnimation");
    public string KeyframeHint { get; private set; } = Localization.Get("Workbench.Keyframe");
    public string ResetHint { get; private set; } = Localization.Get("Workbench.ResetProperty");
    public ICommand SelectCommand { get; }
    public ICommand ToggleAnimationCommand { get; }
    public ICommand AddKeyframeCommand { get; }
    public ICommand ResetCommand { get; }

    internal void Load(AnimationValue value, AnimationTrack? track, bool mixed)
    {
        IsMixed = mixed;
        OnPropertyChanged(nameof(EditingHint));
        IsAnimated = track is not null;
        IsEnabled = track?.Transforms.IsEmpty != false;
        CanAddKeyframe = IsEnabled && session.CanAddEffectPropertyKeyframe(LayerId);
        OnPropertyChanged(nameof(IsOrdered));
        OnPropertyChanged(nameof(CanToggleAnimation));
        OnPropertyChanged(nameof(CanReset));
        RefreshLabels();
        if (HasDraft)
        {
            return;
        }
        loading = true;
        try
        {
            if (value.IsColor)
            {
                Color.Load(value.Color);
            }
            else
            {
                X.Load(value.GetComponent(0));
                Y.Load(value.IsVector ? value.Vector.Y : 0);
            }
            originalX = X.RawText;
            originalY = Y.RawText;
            draftTarget = null;
            draftSource = null;
            Error = null;
            InvalidFieldKey = null;
        }
        finally
        {
            loading = false;
        }
    }

    internal void RefreshLabels()
    {
        DetailsHint = Localization.Get("Workbench.EditAnimationDetails");
        OnPropertyChanged(nameof(DetailsHint));
        KeyframeHint = Localization.Get("Workbench.Keyframe");
        ResetHint = Localization.Get("Workbench.ResetProperty");
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(EditingHint));
        OnPropertyChanged(nameof(AnimationHint));
        OnPropertyChanged(nameof(KeyframeHint));
        OnPropertyChanged(nameof(ResetHint));
    }

    internal void BeginEdit(string panelId) => activePanel = panelId;

    internal double GetOriginalComponent(int component) =>
        double.Parse(component == 0 ? originalX : originalY, NumberStyles.Float, CultureInfo.CurrentCulture);

    internal bool OwnsField(string? field) => field == FieldKey || field == XFieldKey || field == YFieldKey;

    internal ProjectDocument Prepare(ProjectDocument document)
    {
        if (!HasDraft)
        {
            return document;
        }
        session.ViewModel.InvalidPanelId = editingPanel;
        session.ViewModel.InvalidFieldKey = FieldKey;
        if (!ReferenceEquals(draftSource, session.DocumentSnapshot) || draftTarget is null)
        {
            throw new InvalidOperationException(Localization.Get("Workbench.SubtitleDraftConflict"));
        }
        AnimationValue value;
        if (IsColor)
        {
            session.ViewModel.InvalidPanelId = colorEditingPanel;
            if (!Color.TryCommit(out var color))
            {
                throw new InvalidDataException(Color.Error);
            }
            value = color;
        }
        else
        {
            var x = Read(X, XFieldKey, 0);
            value = IsVector ? new ScenePoint(x, Read(Y, YFieldKey, 1)) : x;
        }
        return AnimationEditOperations.SetValue(document, draftTarget, Target, value);
    }

    internal void Accept()
    {
        originalX = X.RawText;
        originalY = Y.RawText;
        if (IsColor)
        {
            Color.Load(Color.Value);
        }
        draftTarget = null;
        draftSource = null;
        Error = null;
        InvalidFieldKey = null;
    }

    internal void Restore(string field)
    {
        loading = true;
        try
        {
            if (field == XFieldKey)
            {
                X.RawText = originalX;
            }
            else if (field == YFieldKey)
            {
                Y.RawText = originalY;
            }
        }
        finally
        {
            loading = false;
        }
        if (!HasDraft)
        {
            draftSource = null;
            draftTarget = null;
        }
        Error = null;
        InvalidFieldKey = null;
        session.NotifyEffectPropertyDraftChanged();
        session.ClearEffectPropertyDraftError(field);
        session.RefreshMaskPreview();
    }

    private double Read(NumericValueDraft draft, string field, int component)
    {
        if (double.TryParse(draft.RawText, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) &&
            double.IsFinite(value) && value >= AnimationPropertyMetadata.GetMinimum(Target.Property, component) &&
            value <= AnimationPropertyMetadata.GetMaximum(Target.Property, component))
        {
            return value;
        }
        session.ViewModel.InvalidPanelId = component == 0 ? xEditingPanel : yEditingPanel;
        session.ViewModel.InvalidFieldKey = field;
        InvalidFieldKey = field;
        Error = Localization.Get("Workbench.InvalidValue");
        throw new InvalidDataException(Error);
    }

    private void OnNumberChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (loading || session.IsUpdating || e.PropertyName != nameof(NumericValueDraft.RawText))
        {
            return;
        }
        if (ReferenceEquals(sender, X))
        {
            xEditingPanel = activePanel;
        }
        else
        {
            yEditingPanel = activePanel;
        }
        Changed();
    }

    private void OnColorChanged(object? sender, EventArgs e)
    {
        if (loading || session.IsUpdating)
        {
            return;
        }
        colorEditingPanel = activePanel;
        Changed();
    }

    private void Changed()
    {
        if (loading || session.IsUpdating)
        {
            return;
        }
        editingPanel = activePanel;
        draftSource ??= session.DocumentSnapshot;
        draftTarget ??= session.AnimationTarget is { } current
            ? current with { LayerId = LayerId, Target = Target } : null;
        session.SceneEditing.DraftTarget ??= draftTarget;
        _ = session.RunCommandAsync(session.PauseForSceneEditAsync);
        session.NotifyEffectPropertyDraftChanged();
        session.RefreshMaskPreview();
    }
}

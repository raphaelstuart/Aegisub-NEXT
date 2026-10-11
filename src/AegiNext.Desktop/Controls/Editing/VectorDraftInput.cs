using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace AegiNext.Desktop.Controls;

/// <summary>以同一行编辑二维向量，保留两个分量的原始草稿；不拥有工程或提交事务。</summary>
public sealed class VectorDraftInput : UserControl
{
    public static readonly StyledProperty<decimal?> XProperty = AvaloniaProperty.Register<VectorDraftInput, decimal?>(nameof(X), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal?> YProperty = AvaloniaProperty.Register<VectorDraftInput, decimal?>(nameof(Y), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> XTextProperty = AvaloniaProperty.Register<VectorDraftInput, string>(nameof(XText), string.Empty, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> YTextProperty = AvaloniaProperty.Register<VectorDraftInput, string>(nameof(YText), string.Empty, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal> MinimumProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Minimum), -1000000000m);
    public static readonly StyledProperty<decimal> MaximumProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Maximum), 1000000000m);
    public static readonly StyledProperty<decimal> IncrementProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Increment), 1m);
    public static readonly StyledProperty<string?> XFieldKeyProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(XFieldKey));
    public static readonly StyledProperty<string?> YFieldKeyProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(YFieldKey));
    public static readonly StyledProperty<string?> XInputNameProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(XInputName));
    public static readonly StyledProperty<string?> YInputNameProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(YInputName));
    private readonly NumericDraftInput xInput;
    private readonly NumericDraftInput yInput;

    /// <summary>创建共用验证范围的 X/Y 输入。</summary>
    public VectorDraftInput()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto,*"), ColumnSpacing = 6 };
        xInput = CreateInput(nameof(X), nameof(XText));
        grid.Children.Add(new NumericDragLabel { Text = "X", Input = xInput, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        Grid.SetColumn(xInput, 1);
        grid.Children.Add(xInput);
        var yLabel = new NumericDragLabel { Text = "Y", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(yLabel, 2);
        grid.Children.Add(yLabel);
        yInput = CreateInput(nameof(Y), nameof(YText));
        yLabel.Input = yInput;
        Grid.SetColumn(yInput, 3);
        grid.Children.Add(yInput);
        Content = grid;
    }

    public decimal? X { get => GetValue(XProperty); set => SetValue(XProperty, value); }
    public decimal? Y { get => GetValue(YProperty); set => SetValue(YProperty, value); }
    public string XText { get => GetValue(XTextProperty); set => SetValue(XTextProperty, value); }
    public string YText { get => GetValue(YTextProperty); set => SetValue(YTextProperty, value); }
    public decimal Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public decimal Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public decimal Increment { get => GetValue(IncrementProperty); set => SetValue(IncrementProperty, value); }
    public string? XFieldKey
    {
        get => GetValue(XFieldKeyProperty);
        set => SetValue(XFieldKeyProperty, value);
    }
    public string? YFieldKey
    {
        get => GetValue(YFieldKeyProperty);
        set => SetValue(YFieldKeyProperty, value);
    }
    public string? XInputName
    {
        get => GetValue(XInputNameProperty);
        set => SetValue(XInputNameProperty, value);
    }
    public string? YInputName
    {
        get => GetValue(YInputNameProperty);
        set => SetValue(YInputNameProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == XFieldKeyProperty || change.Property == XInputNameProperty)
        {
            xInput.Name = XInputName ?? XFieldKey;
        }
        else if (change.Property == YFieldKeyProperty || change.Property == YInputNameProperty)
        {
            yInput.Name = YInputName ?? YFieldKey;
        }
    }

    /// <summary>按稳定分量字段标识定位输入；不依赖宿主 XAML 的名称范围。</summary>
    public bool FocusField(string fieldKey)
    {
        var input = fieldKey == XFieldKey ? xInput : fieldKey == YFieldKey ? yInput : null;
        if (input is null)
        {
            return false;
        }

        return input.FocusInput();
    }

    internal void SetFieldError(string fieldKey, string? error)
    {
        var input = fieldKey == XFieldKey ? xInput : fieldKey == YFieldKey ? yInput : null;
        if (input is not null)
        {
            DataValidationErrors.SetErrors(input, error is null ? null : new[] { error });
        }
    }

    private NumericDraftInput CreateInput(string valueProperty, string textProperty)
    {
        var input = new NumericDraftInput();
        input.Bind(NumericUpDown.ValueProperty, new Binding(valueProperty) { Source = this, Mode = BindingMode.TwoWay });
        input.Bind(NumericDraftInput.RawTextProperty, new Binding(textProperty) { Source = this, Mode = BindingMode.TwoWay });
        input.Bind(NumericUpDown.MinimumProperty, new Binding(nameof(Minimum)) { Source = this });
        input.Bind(NumericUpDown.MaximumProperty, new Binding(nameof(Maximum)) { Source = this });
        input.Bind(NumericUpDown.IncrementProperty, new Binding(nameof(Increment)) { Source = this });
        return input;
    }
}

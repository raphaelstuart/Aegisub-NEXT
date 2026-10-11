using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace AegiNext.Desktop.Controls;

/// <summary>以同一行编辑二维向量，保留两个分量的原始草稿；不拥有工程或提交事务。</summary>
public sealed class VectorDraftInput : UserControl
{
    public static readonly StyledProperty<decimal?> xProperty = AvaloniaProperty.Register<VectorDraftInput, decimal?>(nameof(X), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal?> yProperty = AvaloniaProperty.Register<VectorDraftInput, decimal?>(nameof(Y), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> xTextProperty = AvaloniaProperty.Register<VectorDraftInput, string>(nameof(XText), string.Empty, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> yTextProperty = AvaloniaProperty.Register<VectorDraftInput, string>(nameof(YText), string.Empty, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<decimal> minimumProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Minimum), -1000000000m);
    public static readonly StyledProperty<decimal> maximumProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Maximum), 1000000000m);
    public static readonly StyledProperty<decimal> incrementProperty = AvaloniaProperty.Register<VectorDraftInput, decimal>(nameof(Increment), 1m);
    public static readonly StyledProperty<string?> xFieldKeyProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(XFieldKey));
    public static readonly StyledProperty<string?> yFieldKeyProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(YFieldKey));
    public static readonly StyledProperty<string?> xInputNameProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(XInputName));
    public static readonly StyledProperty<string?> yInputNameProperty = AvaloniaProperty.Register<VectorDraftInput, string?>(nameof(YInputName));
    private readonly NumericDraftInput xInput;
    private readonly NumericDraftInput yInput;
    private readonly Dictionary<AvaloniaProperty, (BindingBase Binding, BindingExpressionBase Expression)> bindings = new();
    private bool xInputNameFrozen;
    private bool yInputNameFrozen;

    /// <summary>创建共用验证范围的 X/Y 输入。</summary>
    public VectorDraftInput()
    {
        var grid = new Grid { ColumnDefinitions = new("Auto,*,Auto,*"), ColumnSpacing = 6 };
        xInput = CreateInput(nameof(X), nameof(XText));
        xInput.AttachedToLogicalTree += (_, _) => xInputNameFrozen = true;
        grid.Children.Add(new NumericDragLabel { Text = "X", Input = xInput, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        Grid.SetColumn(xInput, 1);
        grid.Children.Add(xInput);
        var yLabel = new NumericDragLabel { Text = "Y", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(yLabel, 2);
        grid.Children.Add(yLabel);
        yInput = CreateInput(nameof(Y), nameof(YText));
        yInput.AttachedToLogicalTree += (_, _) => yInputNameFrozen = true;
        yLabel.Input = yInput;
        Grid.SetColumn(yInput, 3);
        grid.Children.Add(yInput);
        Content = grid;
    }

    public decimal? X { get => GetValue(xProperty); set => SetValue(xProperty, value); }
    public decimal? Y { get => GetValue(yProperty); set => SetValue(yProperty, value); }
    public string XText { get => GetValue(xTextProperty); set => SetValue(xTextProperty, value); }
    public string YText { get => GetValue(yTextProperty); set => SetValue(yTextProperty, value); }
    public decimal Minimum { get => GetValue(minimumProperty); set => SetValue(minimumProperty, value); }
    public decimal Maximum { get => GetValue(maximumProperty); set => SetValue(maximumProperty, value); }
    public decimal Increment { get => GetValue(incrementProperty); set => SetValue(incrementProperty, value); }

    /// <summary>接收 X 数值的绑定；清空时只释放此入口持有的绑定。</summary>
    [AssignBinding]
    public BindingBase? XBinding
    {
        get => GetBinding(xProperty);
        set => SetBinding(xProperty, value);
    }

    /// <summary>接收 Y 数值的绑定；保留注册属性的默认绑定模式。</summary>
    [AssignBinding]
    public BindingBase? YBinding
    {
        get => GetBinding(yProperty);
        set => SetBinding(yProperty, value);
    }

    /// <summary>接收 X 原始草稿的绑定，保留未解析和无效文本。</summary>
    [AssignBinding]
    public BindingBase? XTextBinding
    {
        get => GetBinding(xTextProperty);
        set => SetBinding(xTextProperty, value);
    }

    /// <summary>接收 Y 原始草稿的绑定，保留未解析和无效文本。</summary>
    [AssignBinding]
    public BindingBase? YTextBinding
    {
        get => GetBinding(yTextProperty);
        set => SetBinding(yTextProperty, value);
    }

    /// <summary>接收两个分量共用的最小值绑定。</summary>
    [AssignBinding]
    public BindingBase? MinimumBinding
    {
        get => GetBinding(minimumProperty);
        set => SetBinding(minimumProperty, value);
    }

    /// <summary>接收两个分量共用的最大值绑定。</summary>
    [AssignBinding]
    public BindingBase? MaximumBinding
    {
        get => GetBinding(maximumProperty);
        set => SetBinding(maximumProperty, value);
    }

    /// <summary>接收两个分量共用的拖动步长绑定。</summary>
    [AssignBinding]
    public BindingBase? IncrementBinding
    {
        get => GetBinding(incrementProperty);
        set => SetBinding(incrementProperty, value);
    }

    /// <summary>接收 X 分量的稳定字段身份绑定。</summary>
    [AssignBinding]
    public BindingBase? XFieldKeyBinding
    {
        get => GetBinding(xFieldKeyProperty);
        set => SetBinding(xFieldKeyProperty, value);
    }

    /// <summary>接收 Y 分量的稳定字段身份绑定。</summary>
    [AssignBinding]
    public BindingBase? YFieldKeyBinding
    {
        get => GetBinding(yFieldKeyProperty);
        set => SetBinding(yFieldKeyProperty, value);
    }

    /// <summary>接收 X 分量输入控件的名称绑定。</summary>
    [AssignBinding]
    public BindingBase? XInputNameBinding
    {
        get => GetBinding(xInputNameProperty);
        set => SetBinding(xInputNameProperty, value);
    }

    /// <summary>接收 Y 分量输入控件的名称绑定。</summary>
    [AssignBinding]
    public BindingBase? YInputNameBinding
    {
        get => GetBinding(yInputNameProperty);
        set => SetBinding(yInputNameProperty, value);
    }

    public string? XFieldKey
    {
        get => GetValue(xFieldKeyProperty);
        set => SetValue(xFieldKeyProperty, value);
    }
    public string? YFieldKey
    {
        get => GetValue(yFieldKeyProperty);
        set => SetValue(yFieldKeyProperty, value);
    }
    public string? XInputName
    {
        get => GetValue(xInputNameProperty);
        set => SetValue(xInputNameProperty, value);
    }
    public string? YInputName
    {
        get => GetValue(yInputNameProperty);
        set => SetValue(yInputNameProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (!xInputNameFrozen && (change.Property == xFieldKeyProperty || change.Property == xInputNameProperty))
        {
            xInput.Name = XInputName ?? XFieldKey;
        }
        else if (!yInputNameFrozen && (change.Property == yFieldKeyProperty || change.Property == yInputNameProperty))
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

    private BindingBase? GetBinding(AvaloniaProperty property)
    {
        VerifyAccess();
        return bindings.TryGetValue(property, out var current) ? current.Binding : null;
    }

    private void SetBinding(AvaloniaProperty property, BindingBase? binding)
    {
        VerifyAccess();
        var hasPrevious = bindings.TryGetValue(property, out var previous);
        if (hasPrevious && ReferenceEquals(previous.Binding, binding))
        {
            return;
        }

        if (binding is null)
        {
            if (hasPrevious)
            {
                bindings.Remove(property);
                previous.Expression.Dispose();
            }

            return;
        }

        var expression = Bind(property, binding);
        bindings[property] = (binding, expression);
        if (hasPrevious)
        {
            previous.Expression.Dispose();
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

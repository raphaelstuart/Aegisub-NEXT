using AegiNext.Desktop.I18n;
using Avalonia.Controls;
using Avalonia;

namespace AegiNext.Desktop.Menus;

internal sealed class WindowMenuBar : UserControl, IDisposable
{
    private readonly WorkbenchMenuCatalog catalog;
    private readonly Menu fullMenu = new() { Name = "MainMenu" };
    private readonly Menu overflowMenu = new();
    private readonly MenuItem overflow = new() { Header = "☰" };
    private readonly List<WindowMenuGroupProjection> projections = [];
    private double availableWidth = double.PositiveInfinity;

    internal WindowMenuBar(WorkbenchMenuCatalog catalog, IEnumerable<WorkbenchMenuGroup>? groups = null)
    {
        this.catalog = catalog;
        foreach (var group in groups ?? WorkbenchMenuCatalog.Groups)
        {
            var main = new WindowMenuGroupProjection(group, catalog);
            var compact = new WindowMenuGroupProjection(group, catalog);
            projections.Add(main);
            projections.Add(compact);
            fullMenu.Items.Add(main.Item);
            overflow.Items.Add(compact.Item);
        }
        overflowMenu.Items.Add(overflow);
        Content = fullMenu;
        catalog.Changed += OnChanged;
        AttachedToVisualTree += OnAttached;
        Refresh();
    }

    internal void SetAvailableWidth(double width)
    {
        availableWidth = Math.Max(0, width);
        RefreshOverflow();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        catalog.Changed -= OnChanged;
        AttachedToVisualTree -= OnAttached;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        fullMenu.Measure(new(double.PositiveInfinity, 40));
        RefreshOverflow();
    }

    private void Refresh()
    {
        ToolTip.SetTip(overflow, Localization.Get("Workbench.View"));
        foreach (var projection in projections)
        {
            projection.Refresh();
        }
        fullMenu.Measure(new(double.PositiveInfinity, 40));
        RefreshOverflow();
    }

    private void RefreshOverflow()
    {
        var next = fullMenu.DesiredSize.Width > availableWidth ? overflowMenu : fullMenu;
        if (!ReferenceEquals(Content, next))
        {
            Content = next;
        }
    }
}

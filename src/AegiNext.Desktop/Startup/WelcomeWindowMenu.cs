using AegiNext.Desktop.Controls.Common;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Menus;
using AegiNext.Desktop.Settings;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Styling;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace AegiNext.Desktop.Startup;

internal sealed class WelcomeWindowMenu : IDisposable
{
    private readonly Window window;
    private readonly WindowTitleBar titleBar;
    private readonly WorkbenchMenuCatalog catalog;
    private readonly WindowMenuBar menuBar;
    private readonly WorkbenchNativeMenu nativeMenu;
    private readonly Dictionary<WorkbenchCommand, Bitmap> icons = [];

    internal WelcomeWindowMenu(Window window, WindowTitleBar titleBar, WorkbenchMenuCatalog catalog)
    {
        this.window = window;
        this.titleBar = titleBar;
        this.catalog = catalog;
        var groups = WorkbenchMenuCatalog.Groups.Where(group => group.Key == "Help").ToArray();
        menuBar = new(catalog, groups);
        nativeMenu = new(groups, catalog.GetCommand, command =>
        {
            var icon = WorkbenchIcon.CreateNative(command.ToString());
            icons.Add(command, icon);
            return icon;
        });
        NativeMenu.SetMenu(window, nativeMenu.Menu);
        catalog.Changed += OnCatalogChanged;
        titleBar.PropertyChanged += OnTitleBarChanged;
        Refresh();
    }

    internal void UpdatePreferences(WorkbenchPreferences value)
    {
        var windowMenu = !OperatingSystem.IsMacOS() || value.WindowMenuOnMac;
        titleBar.MenuContent = windowMenu ? menuBar : null;
        nativeMenu.SetEnabled(!OperatingSystem.IsMacOS() || !windowMenu);
        Refresh();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        catalog.Changed -= OnCatalogChanged;
        titleBar.PropertyChanged -= OnTitleBarChanged;
        titleBar.MenuContent = null;
        NativeMenu.SetMenu(window, null);
        menuBar.Dispose();
        foreach (var icon in icons.Values)
        {
            icon.Dispose();
        }
        icons.Clear();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => Refresh();

    private void OnTitleBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowTitleBar.AvailableMenuWidthProperty)
        {
            menuBar.SetAvailableWidth(titleBar.AvailableMenuWidth);
        }
    }

    private void Refresh()
    {
        nativeMenu.Update(key => Localization.Get("Workbench." + key), catalog.GetDisplayLabel, catalog.GetGestureLabel);
        menuBar.SetAvailableWidth(titleBar.AvailableMenuWidth);
    }
}

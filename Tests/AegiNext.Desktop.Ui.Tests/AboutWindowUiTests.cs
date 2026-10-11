using System.Reflection;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Shortcuts;
using AegiNext.Desktop.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AegiNext.Desktop.Ui.Tests;

/// <summary>验证关于窗口的菜单入口、应用元数据、本地化和生命周期。</summary>
public sealed class AboutWindowUiTests
{
    /// <summary>大字号名称的布局必须容纳字体自然行高，避免裁切下伸字形。</summary>
    [AvaloniaTheory]
    [InlineData(false, "AegiNext")]
    [InlineData(true, "AegiNext")]
    [InlineData(false, "关于 AegiNext 2026")]
    [InlineData(true, "关于 AegiNext 2026")]
    public void ProductHeadingFitsNaturalFontMetrics(bool darkTheme, string productName)
    {
        using var environment = new UiTestEnvironment();
        var window = new AboutWindow
        {
            RequestedThemeVariant = darkTheme ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            var heading = UiTestActions.Find<TextBlock>(window, "AboutProductName");
            heading.Text = productName;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var naturalLayout = new TextLayout(productName,
                new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight, heading.FontStretch),
                heading.FontSize, heading.Foreground);

            Assert.True(heading.TextLayout.Height >= naturalLayout.Height,
                $"标题行高 {heading.TextLayout.Height} 小于字体所需高度 {naturalLayout.Height}。");
            Assert.True(heading.Bounds.Height >= naturalLayout.Height,
                $"标题控件高度 {heading.Bounds.Height} 小于字体所需高度 {naturalLayout.Height}。");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>统一菜单打开唯一窗口，并显示发布元数据和当前年份版权。</summary>
    [AvaloniaFact]
    public async Task HelpMenuOpensOneAboutWindowAndReopeningRegistersANewWindow()
    {
        await using var context = new MainWindowTestContext();
        var main = context.Window;
        var menu = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(main));
        var help = Assert.Single(menu.Items.OfType<NativeMenuItem>(), item => item.Header == "Help");
        var command = main.GetCommand(WorkbenchCommand.OPEN_ABOUT);
        var about = Assert.Single(help.Menu!.Items.OfType<NativeMenuItem>(), item => ReferenceEquals(item.Command, command));
        Assert.Equal("About", about.Header);
        Assert.Same(command, about.Command);
        Assert.True(command.CanExecute(null));

        command.Execute(null);
        var window = Assert.Single(main.OwnedWindows.OfType<AboutWindow>());
        Assert.Contains(window, context.WindowRegistry.Windows);
        Assert.Equal("AegiNext", UiTestActions.Find<TextBlock>(window, "AboutProductName").Text);
        var assemblyVersion = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(assemblyVersion, UiTestActions.Find<TextBlock>(window, "AboutVersion").Text);
        Assert.Equal($"Copyright © {DateTime.Now.Year} yosymph.org.", UiTestActions.Find<TextBlock>(window, "AboutCopyright").Text);
        Assert.Equal("Opensource under GPLv3 license.", UiTestActions.Find<TextBlock>(window, "AboutLicense").Text);
        var image = UiTestActions.Find<Image>(window, "AboutApplicationIcon");
        var bitmap = Assert.IsType<Bitmap>(image.Source);
        Assert.Equal(256, bitmap.PixelSize.Width);
        Assert.Equal(256, bitmap.PixelSize.Height);

        command.Execute(null);
        Assert.Same(window, Assert.Single(main.OwnedWindows.OfType<AboutWindow>()));
        UiTestActions.Click(window, "AboutCloseButton");
        Assert.False(window.IsVisible);
        Assert.DoesNotContain(window, context.WindowRegistry.Windows);

        command.Execute(null);
        var reopened = Assert.Single(main.OwnedWindows.OfType<AboutWindow>());
        Assert.NotSame(window, reopened);
        Assert.Contains(reopened, context.WindowRegistry.Windows);
        reopened.Close();
    }

    /// <summary>语言和主题切换即时更新标题与菜单，信息区域保持完整布局。</summary>
    [AvaloniaFact]
    public async Task AboutWindowRefreshesLanguageAndFitsItsContentInBothThemes()
    {
        await using var context = new MainWindowTestContext();
        context.Window.GetCommand(WorkbenchCommand.OPEN_ABOUT).Execute(null);
        var window = Assert.Single(context.Window.OwnedWindows.OfType<AboutWindow>());
        try
        {
            Assert.Equal("About AegiNext", window.Title);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                Localization.SetLanguage("zh-CN");
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.Equal("关于 AegiNext", window.Title);
                Assert.Equal("关于 AegiNext", window.TitleBar.Title);
                Assert.Equal("关闭", UiTestActions.Find<Button>(window, "AboutCloseButton").Content);
                var root = Assert.IsType<NativeMenu>(NativeMenu.GetMenu(context.Window));
                var help = Assert.Single(root.Items.OfType<NativeMenuItem>(), item => item.Header == "帮助");
                var aboutCommand = context.Window.GetCommand(WorkbenchCommand.OPEN_ABOUT);
                var aboutItem = Assert.Single(help.Menu!.Items.OfType<NativeMenuItem>(), item => ReferenceEquals(item.Command, aboutCommand));
                Assert.Equal("关于", aboutItem.Header);
                foreach (var name in new[] { "AboutApplicationIcon", "AboutProductName", "AboutVersion", "AboutCopyright", "AboutLicense", "AboutCloseButton" })
                {
                    var control = UiTestActions.Find<Control>(window, name);
                    Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0, name);
                    var origin = control.TranslatePoint(default, window)!.Value;
                    Assert.InRange(origin.X, 0, window.ClientSize.Width - control.Bounds.Width);
                    Assert.InRange(origin.Y, 0, window.ClientSize.Height - control.Bounds.Height);
                }
                Localization.SetLanguage("en-US");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("About AegiNext", window.Title);
                Assert.Equal("Close", UiTestActions.Find<Button>(window, "AboutCloseButton").Content);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>主窗口释放时关闭关于窗口并清除窗口注册。</summary>
    [AvaloniaFact]
    public async Task DisposingMainWindowClosesItsAboutWindow()
    {
        var context = new MainWindowTestContext();
        var window = context.Window;
        window.GetCommand(WorkbenchCommand.OPEN_ABOUT).Execute(null);
        var about = Assert.Single(window.OwnedWindows.OfType<AboutWindow>());
        await context.DisposeAsync();
        Assert.False(about.IsVisible);
        Assert.Empty(context.WindowRegistry.Windows);
    }
}

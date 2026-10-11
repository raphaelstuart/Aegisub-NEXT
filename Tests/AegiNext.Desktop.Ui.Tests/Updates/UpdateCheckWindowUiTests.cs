using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Updates;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ColorTextBlock.Avalonia;
using Markdown.Avalonia;

namespace AegiNext.Desktop.Ui.Tests.Updates;

/// <summary>验证更新窗口的实际 Markdown 呈现、外部链接和状态反馈。</summary>
public sealed class UpdateCheckWindowUiTests
{
    private const string RELEASE_NOTES = """
        # Subtitle improvements

        Release notes with **bold text** and [documentation](https://github.com/Yohuke-no-Symphony/Aegisub-NEXT).

        - Better subtitle editing
        - 更完整的字幕预览

        ```text
        Subtitle preview fixture
        ```

        | Change | Result |
        | --- | --- |
        | Update check | Available |
        """;

    /// <summary>发布说明转换为可读控件，并在深浅主题与窄窗口中支持滚动。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AvailableReleaseRendersMarkdownAndScrollsInBothThemes(bool dark)
    {
        using var environment = new UiTestEnvironment();
        var notes = RELEASE_NOTES + "\n\n" + string.Join("\n\n", Enumerable.Repeat("Long release note paragraph 中文 123.", 30));
        var release = CreateRelease(notes);
        var model = new UpdateCheckViewModel(new(UpdateCheckStatus.UPDATE_AVAILABLE, "2026.10.11", release),
            UpdateChannel.STABLE, () => Task.CompletedTask, _ => Task.FromResult(true));
        var window = new UpdateCheckWindow(model)
        {
            Width = 640,
            Height = 480,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Contains("2026.10.11", UiTestActions.Find<TextBlock>(window, "CurrentVersionText").Text!, StringComparison.Ordinal);
            Assert.Contains("2099.1.2.3", UiTestActions.Find<TextBlock>(window, "AvailableVersionText").Text!, StringComparison.Ordinal);
            var markdown = UiTestActions.Find<MarkdownScrollViewer>(window, "ReleaseNotesViewer");
            Assert.Equal(notes, markdown.Markdown);
            Assert.Equal("FluentTheme", markdown.MarkdownStyleName);
            var text = markdown.GetVisualDescendants().OfType<CTextBlock>().ToArray();
            Assert.Contains(text, block => block.Classes.Contains("Heading1") && block.Text.Contains("Subtitle improvements", StringComparison.Ordinal));
            Assert.Contains(text, block => block.Text.Contains("Better subtitle editing", StringComparison.Ordinal));
            Assert.Contains(markdown.GetVisualDescendants().OfType<Grid>(), grid => grid.Classes.Contains("List"));
            Assert.Contains(markdown.GetVisualDescendants().OfType<Grid>(), grid => grid.Classes.Contains("Table"));
            var code = Assert.Single(markdown.GetVisualDescendants().OfType<TextBlock>(), block => block.Classes.Contains("CodeBlock"));
            Assert.Contains("Subtitle preview fixture", code.Text!, StringComparison.Ordinal);
            Assert.DoesNotContain("```", code.Text!, StringComparison.Ordinal);
            var scroll = Assert.Single(markdown.GetVisualDescendants().OfType<ScrollViewer>(), viewer => !viewer.Classes.Contains("CodeBlock"));
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.ScrollToEnd();
            Dispatcher.UIThread.RunJobs();
            Assert.True(scroll.Offset.Y > 0);

            foreach (var name in new[] { "UpdateReleaseButton", "UpdateCloseButton" })
            {
                var button = UiTestActions.Find<Button>(window, name);
                var point = button.TranslatePoint(default, window)!.Value;
                Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
                Assert.InRange(point.X, 0, window.ClientSize.Width - button.Bounds.Width);
                Assert.InRange(point.Y, 0, window.ClientSize.Height - button.Bounds.Height);
            }

            var artifactDirectory = Environment.GetEnvironmentVariable("AEGINEXT_UPDATES_ARTIFACTS");
            if (!string.IsNullOrWhiteSpace(artifactDirectory))
            {
                Directory.CreateDirectory(artifactDirectory);
                scroll.ScrollToHome();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(artifactDirectory, $"updates-{(dark ? "dark" : "light")}-640.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>非浏览器链接被拒绝，不会交给操作系统启动器执行。</summary>
    [AvaloniaTheory]
    [InlineData("file:///tmp/update-fixture.txt")]
    [InlineData("javascript:alert('fixture')")]
    public async Task UnsupportedMarkdownLinksStayInTheWindowWithoutCallingTheLauncher(string address)
    {
        using var environment = new UiTestEnvironment();
        var launchCount = 0;
        var model = new UpdateCheckViewModel(new(UpdateCheckStatus.UPDATE_AVAILABLE, "2026.10.11", CreateRelease(RELEASE_NOTES)),
            UpdateChannel.STABLE, () => Task.CompletedTask, _ =>
            {
                launchCount++;
                return Task.FromResult(true);
            });
        var window = new UpdateCheckWindow(model);
        try
        {
            window.Show();
            await model.OpenLinkCommand.ExecuteAsync(address);

            Assert.Equal(0, launchCount);
            Assert.True(window.IsVisible);
            Assert.True(model.HasError);
            var error = UiTestActions.Find<TextBlock>(window, "UpdateErrorText");
            Assert.True(error.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(error.Text));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>操作系统启动器抛出异常时提示保留在当前窗口，不泄漏命令异常。</summary>
    [AvaloniaFact]
    public async Task LauncherExceptionsBecomeVisibleWindowFeedback()
    {
        using var environment = new UiTestEnvironment();
        var model = new UpdateCheckViewModel(new(UpdateCheckStatus.UPDATE_AVAILABLE, "2026.10.11", CreateRelease(RELEASE_NOTES)),
            UpdateChannel.STABLE, () => Task.CompletedTask,
            _ => Task.FromException<bool>(new IOException("Browser fixture unavailable")));
        var window = new UpdateCheckWindow(model);
        try
        {
            window.Show();
            await model.OpenReleaseCommand.ExecuteAsync(null);

            Assert.True(window.IsVisible);
            Assert.True(model.HasError);
            var error = UiTestActions.Find<TextBlock>(window, "UpdateErrorText");
            Assert.True(error.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(error.Text));
            Assert.Null(model.OpenReleaseCommand.ExecutionTask!.Exception);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>发布按钮和 Markdown 链接使用注入的启动器，失败原因保留在窗口中。</summary>
    [AvaloniaFact]
    public async Task ReleaseAndMarkdownLinksUseTheLauncherAndReportFailures()
    {
        using var environment = new UiTestEnvironment();
        var launched = new List<Uri>();
        var launchSucceeds = false;
        var release = CreateRelease(RELEASE_NOTES);
        var model = new UpdateCheckViewModel(new(UpdateCheckStatus.UPDATE_AVAILABLE, "2026.10.11", release),
            UpdateChannel.STABLE, () => Task.CompletedTask, uri =>
            {
                launched.Add(uri);
                return Task.FromResult(launchSucceeds);
            });
        var window = new UpdateCheckWindow(model);
        try
        {
            window.Show();
            var releaseButton = UiTestActions.Find<Button>(window, "UpdateReleaseButton");
            Assert.Same(model.OpenReleaseCommand, releaseButton.Command);

            await model.OpenReleaseCommand.ExecuteAsync(null);
            Assert.Equal(release.PageUri, Assert.Single(launched));
            Assert.True(window.IsVisible);
            Assert.True(model.HasError);
            var error = UiTestActions.Find<TextBlock>(window, "UpdateErrorText");
            Assert.True(error.IsVisible);
            Assert.False(string.IsNullOrWhiteSpace(error.Text));

            launchSucceeds = true;
            await model.OpenLinkCommand.ExecuteAsync("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT");
            Assert.Equal(2, launched.Count);
            Assert.Equal("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT", launched[1].AbsoluteUri);
            var markdown = UiTestActions.Find<MarkdownScrollViewer>(window, "ReleaseNotesViewer");
            Assert.Same(model.OpenLinkCommand, markdown.Plugins.HyperlinkCommand);

            UiTestActions.Click(window, "UpdateCloseButton");
            Assert.False(window.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>无更新和失败都有明确反馈，失败可以重试且语言切换保留结果。</summary>
    [AvaloniaTheory]
    [InlineData((int)UpdateCheckStatus.UP_TO_DATE)]
    [InlineData((int)UpdateCheckStatus.NO_RELEASE)]
    [InlineData((int)UpdateCheckStatus.FAILED)]
    public async Task NonReleaseStatesShowLocalizedFeedbackAndFailureCanRetry(int statusValue)
    {
        using var environment = new UiTestEnvironment();
        var status = (UpdateCheckStatus)statusValue;
        var retryCount = 0;
        var error = status == UpdateCheckStatus.FAILED ? new IOException("Offline fixture") : null;
        var model = new UpdateCheckViewModel(new(status, "2026.10.11", Error: error), UpdateChannel.STABLE,
            () =>
            {
                retryCount++;
                return Task.CompletedTask;
            }, _ => Task.FromResult(true));
        var window = new UpdateCheckWindow(model);
        try
        {
            window.Show();
            Assert.False(model.HasRelease);
            Assert.False(UiTestActions.Find<MarkdownScrollViewer>(window, "ReleaseNotesViewer").IsEffectivelyVisible);
            Assert.False(UiTestActions.Find<Button>(window, "UpdateReleaseButton").IsVisible);
            var message = UiTestActions.Find<TextBlock>(window, "UpdateMessageText");
            Assert.False(string.IsNullOrWhiteSpace(message.Text));
            var initialMessage = message.Text;
            var retry = UiTestActions.Find<Button>(window, "UpdateRetryButton");
            Assert.Equal(status == UpdateCheckStatus.FAILED, retry.IsVisible);
            Assert.Same(model.RetryCommand, retry.Command);
            if (status == UpdateCheckStatus.FAILED)
            {
                await model.RetryCommand.ExecuteAsync(null);
                Assert.Equal(1, retryCount);
            }

            Localization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.NotEqual(initialMessage, message.Text);
            Assert.False(string.IsNullOrWhiteSpace(window.Title));
            Assert.Contains("2026.10.11", UiTestActions.Find<TextBlock>(window, "CurrentVersionText").Text!, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private static UpdateRelease CreateRelease(string notes)
    {
        return new(new(2099, 1, 2, 3), "v2099.1.2.3", "Update fixture", notes,
            new("https://github.com/Yohuke-no-Symphony/Aegisub-NEXT/releases/tag/v2099.1.2.3"));
    }
}

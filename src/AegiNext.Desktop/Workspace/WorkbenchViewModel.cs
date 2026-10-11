using System.Windows.Input;
using AegiNext.Desktop.Panels.Preview;
using AegiNext.Desktop.Panels.Timeline;
using AegiNext.Desktop.Panels.Subtitles;
using AegiNext.Desktop.Panels.Styles;
using AegiNext.Desktop.Panels.Effects;
using AegiNext.Desktop.Panels.Masks;
using AegiNext.Desktop.Panels.Export;
using AegiNext.Desktop.Panels.Log;
using AegiNext.Desktop.Shortcuts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Workspace;

internal sealed class WorkbenchViewModel : ObservableObject
{
    private readonly WorkbenchSession session;
    private readonly Dictionary<WorkbenchCommand, AsyncRelayCommand> commands = [];
    private string title = "AegiNEXT";
    private string? error;
    private bool isBusy;
    private string? invalidPanelId;
    private string? invalidFieldKey;

    internal WorkbenchViewModel(WorkbenchSession session)
    {
        this.session = session;
        Preview = new(session);
        Timeline = new(session);
        Subtitles = new(session);
        Styles = new(session);
        Effects = new(session);
        Masks = new(session);
        Export = new(session);
        Log = new(session.Journal);
        foreach (var command in Enum.GetValues<WorkbenchCommand>())
        {
            commands.Add(command, new(() => ExecuteCommandAsync(command), () =>
                    session.CanExecuteCommand(command) && (ContextCommandAvailability?.Invoke(command) ?? true),
                AsyncRelayCommandOptions.AllowConcurrentExecutions));
        }
    }

    public event EventHandler<WorkbenchHostCommandEventArgs>? HostCommandRequested;
    internal Func<WorkbenchHostCommandEventArgs, Task>? HostCommandHandler { get; set; }
    internal Func<WorkbenchCommand, bool?>? ContextCommandAvailability { get; set; }
    internal Func<WorkbenchCommand, bool>? TryExecuteContextCommand { get; set; }
    public event EventHandler? GesturesCancelled;
    public event EventHandler? DraftErrorFocusRequested;
    internal event Action<string>? PanelActivationRequested;
    public PreviewPanelViewModel Preview { get; }
    public TimelinePanelViewModel Timeline { get; }
    public SubtitlesPanelViewModel Subtitles { get; }
    public StylesPanelViewModel Styles { get; }
    public EffectsPanelViewModel Effects { get; }
    public MaskPanelViewModel Masks { get; }
    public ExportPanelViewModel Export { get; }
    public LogPanelViewModel Log { get; }
    public string Title
    {
        get => title;
        internal set => SetProperty(ref title, value);
    }
    public string? Error
    {
        get => error;
        internal set => SetProperty(ref error, value);
    }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsBusy
    {
        get => isBusy;
        internal set => SetProperty(ref isBusy, value);
    }
    public string? InvalidPanelId
    {
        get => invalidPanelId;
        internal set => SetProperty(ref invalidPanelId, value);
    }
    public string? InvalidFieldKey
    {
        get => invalidFieldKey;
        internal set => SetProperty(ref invalidFieldKey, value);
    }
    /// <summary>取得菜单、快捷键和面板共同使用的命令实例。</summary>
    public ICommand GetCommand(WorkbenchCommand command) => commands[command];
    /// <summary>等待指定工作流完成，沿用会话的验证和错误处理边界。</summary>
    public Task ExecuteCommandAsync(WorkbenchCommand command) => TryExecuteContextCommand?.Invoke(command) == true
        ? Task.CompletedTask : session.ExecuteCommandAsync(command);
    /// <summary>验证各面板草稿，并将全部有效修改合并为一次工程事务。</summary>
    public bool TryCommitDrafts() => session.TryCommitDrafts();
    /// <summary>取消未完成的预览、时间线和面板指针手势。</summary>
    public void CancelGestures()
    {
        session.CancelInteractiveSeeking();
        Preview.IsScrubbing = false;
        Timeline.IsSeeking = false;
        GesturesCancelled?.Invoke(this, EventArgs.Empty);
    }

    internal async Task RequestHostCommandAsync(WorkbenchCommand command, AegiNext.Desktop.Settings.SettingsPage? page = null)
    {
        var request = new WorkbenchHostCommandEventArgs(command, page);
        if (HostCommandHandler is { } handler)
        {
            await handler(request);
        }
        else
        {
            HostCommandRequested?.Invoke(this, request);
        }
    }

    internal void ActivatePanel(string panelId) => PanelActivationRequested?.Invoke(panelId);

    internal void FocusDraftError() => DraftErrorFocusRequested?.Invoke(this, EventArgs.Empty);
    internal void RefreshCommands()
    {
        Subtitles.RefreshMoveCommand();
        Subtitles.MergeCueCommand.NotifyCanExecuteChanged();
        Timeline.RefreshMoveCommand();
        foreach (var command in commands.Values)
        {
            command.NotifyCanExecuteChanged();
        }

        OnPropertyChanged(nameof(HasError));
    }
}

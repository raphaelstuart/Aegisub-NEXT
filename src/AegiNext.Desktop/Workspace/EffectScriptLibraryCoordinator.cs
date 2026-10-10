using AegiNext.Application.Presets;
using AegiNext.Application.Tasks;
using AegiNext.Core.Effects;
using AegiNext.Core.Projects;
using AegiNext.Desktop.I18n;
using AegiNext.Desktop.Workspace.Diagnostics;

namespace AegiNext.Desktop.Workspace;

internal sealed class EffectScriptLibraryCoordinator(WorkbenchSession session, IWorkbenchDialogService dialogs)
{
    private int queuedOperations;
    private EffectScriptChoice[] choices = [];
    private readonly Lock operationsGate = new();
    private readonly HashSet<Task> operations = [];
    public Task Completion
    {
        get
        {
            lock (operationsGate)
            {
                return Task.WhenAll(operations);
            }
        }
    }
    internal bool IsBusy => queuedOperations > 0 || session.ApplicationContext.EffectsBusy;

    internal void Initialize() => Queue(async () =>
    {
        await session.ApplicationContext.Initialization;
        RefreshChoices();
    });

    internal void Queue(Func<Task> action, Action<WorkbenchLogEntry>? onFailure = null)
    {
        queuedOperations++;
        var operation = RunAsync(action, onFailure);
        lock (operationsGate)
        {
            operations.Add(operation);
        }
        _ = ForgetOperationAsync(operation);
    }

    private async Task ForgetOperationAsync(Task operation)
    {
        await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        lock (operationsGate)
        {
            operations.Remove(operation);
        }
    }

    private async Task RunAsync(Func<Task> action, Action<WorkbenchLogEntry>? onFailure)
    {
        try
        {
            if (!session.IsClosing)
            {
                await session.RunCommandAsync(action, onFailure);
            }
        }
        finally
        {
            queuedOperations--;
        }
    }

    internal void RefreshChoices()
    {
        var vm = session.ViewModel.Effects;
        var selectedId = vm.Preset >= 0 && vm.Preset < choices.Length ? choices[vm.Preset].Id : null;
        choices = BuiltinEffectScripts.Templates.Select(template => new EffectScriptChoice(template.Script.Id,
            BuiltinName(template.Script.Id), template.Source))
            .Concat(session.EffectScriptLibrary.Snapshot.Presets.Select(preset => new EffectScriptChoice(preset.Id.ToString("D"), preset.Name, preset.Source)))
            .ToArray();
        var names = choices.Select(choice => choice.Name).ToArray();
        if (!vm.Presets.SequenceEqual(names))
        {
            vm.Presets = names;
        }
        var index = Array.FindIndex(choices, choice => choice.Id == selectedId);
        vm.Preset = choices.Length == 0 ? -1 : Math.Max(0, index);
    }

    internal Task ApplySelectedAsync()
    {
        var index = session.ViewModel.Effects.Preset;
        if (index >= 0 && index < choices.Length)
        {
            return ApplyAsync(choices[index].Source);
        }
        return Task.CompletedTask;
    }

    internal Task ApplyAsync(string source)
    {
        if (session.IsProjectBusy || session.IsClosing || !session.TryCommitDrafts())
        {
            return Task.CompletedTask;
        }
        var target = session.SceneEditing.Target;
        AnimationTrackTarget? context = target.TextRangeId is not null || target.State != SubtitleAnimationState.NORMAL ? target : null;
        var selected = target.TextRangeId is not null && session.SelectedLayer is { } rangeLayer
            ? new HashSet<Guid> { rangeLayer.Id } : session.TimelineClipIds().ToHashSet();
        var layerIds = session.Editor.Snapshot.Layers
            .Where(layer => layer.SubtitleId is not null && selected.Contains(layer.Id)).Select(layer => layer.Id).ToArray();
        if (layerIds.Length == 0)
        {
            return Task.CompletedTask;
        }
        session.ViewModel.CancelGestures();
        session.ClearKeyframeSelection();
        return session.ApplicationContext.Tasks.Submit(new ApplyEffectScriptTask(session, source, layerIds,
            session.Editor.Snapshot, session.TaskInputRevision, context)).Completion;
    }

    internal async Task UpsertAsync(EffectScriptPreset preset)
    {
        await session.ApplicationContext.RunEffectOperationAsync(() => session.EffectScriptLibrary.UpsertAsync(preset));
        RefreshChoices();
        session.NotifyEffectLibraryChanged();
        session.LogInfo("Effects", Localization.Get("Workbench.SavePreset"), preset.Name);
    }

    internal async Task DeleteAsync(Guid id)
    {
        await session.ApplicationContext.RunEffectOperationAsync(() => session.EffectScriptLibrary.RemoveAsync(id));
        RefreshChoices();
        session.NotifyEffectLibraryChanged();
    }

    internal async Task ImportAsync()
    {
        var path = await dialogs.OpenFileAsync("ImportEffectScripts", "EffectScriptFiles", ["*.aegifx"]);
        if (path is null)
        {
            return;
        }
        await session.ApplicationContext.Tasks.Submit(new ImportEffectScriptsTask(session, path)).Completion;
        RefreshChoices();
        session.NotifyEffectLibraryChanged();
    }

    internal async Task ExportAsync(EffectScriptPreset preset)
    {
        var script = EffectScriptParser.Parse(preset.Source);
        var path = await dialogs.SaveFileAsync("ExportEffectScripts", "EffectScriptFiles", ["*.aegifx"], ".aegifx", script.Id + ".aegifx");
        if (path is not null)
        {
            await session.ApplicationContext.Tasks.Submit(new ExportEffectScriptTask(session, path, preset.Source)).Completion;
        }
    }

    private static string BuiltinName(string id) => Localization.Get("Workbench." + (id switch
    {
        "fade-in-out" => "Fade", "fade-in" => "FadeIn", "fade-out" => "FadeOut",
        "pop-in" => "Pop", "pop-out" => "PopOut", "slide-in" => "Slide", "slide-out" => "SlideOut",
        "letter-bounce" => "LetterBounce", "letter-pulse" => "LetterPulse",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    }));
}

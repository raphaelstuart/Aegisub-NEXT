using System.Collections.Immutable;
using AegiNext.Application.Presets;
using AegiNext.Core.Effects;
using AegiNext.Desktop.Settings.Effects;
using CommunityToolkit.Mvvm.Input;

namespace AegiNext.Desktop.Tests;

public sealed class EffectSettingsViewModelTests
{
    [Fact]
    public async Task LanguageRefreshPreservesIndependentDraftAndDiagnosticsDuringTransientSelectionReset()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        model.Name = "未保存 Script ABC 123";
        model.Source = "effect broken\n";
        model.ValidateCommand.Execute(null);
        var draft = model.Draft;
        var error = model.Error;
        var column = model.DiagnosticColumn;
        var resets = 0;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.Effects))
            {
                resets++;
                model.SelectedEffect = null;
            }
        };

        model.RefreshLanguage();
        model.UpdateEffects([]);

        Assert.Equal(2, resets);
        Assert.Equal(draft, model.Draft);
        Assert.Null(model.SelectedEffect);
        Assert.Empty(model.SelectedIds);
        Assert.Equal("未保存 Script ABC 123", model.Name);
        Assert.Equal("effect broken\n", model.Source);
        Assert.Equal(error, model.Error);
        Assert.Equal(1, model.DiagnosticLine);
        Assert.Equal(column, model.DiagnosticColumn);
        Assert.True(model.IsDirty);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("save")]
    public async Task ValidationFailureEmitsTheOriginalExceptionOnceWithoutSubmittingOrRepeatingOnRefresh(string operation)
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        model.Source = "effect broken\n";
        var failures = new List<Exception>();
        var submissions = 0;
        model.ValidationFailed += (_, e) => failures.Add(e.Error);
        model.SaveRequested += (_, _) => submissions++;
        IRelayCommand command = operation == "save" ? model.SaveCommand : model.ValidateCommand;
        command.Execute(null);
        var error = Assert.IsType<EffectScriptException>(Assert.Single(failures));
        Assert.Equal(model.Error, error.Message);
        Assert.Equal(model.DiagnosticLine, error.Line);
        Assert.Equal(model.DiagnosticColumn, error.Column);
        Assert.Equal(0, submissions);
        model.UpdateEffects([]);
        model.RefreshLanguage();
        model.IsBusy = true;
        model.IsBusy = false;
        Assert.Single(failures);
        command.Execute(null);
        Assert.Equal(2, failures.Count);
    }

    [Fact]
    public void BuiltinsAreReadOnlyAndCannotBeDeletedOrSaved()
    {
        var model = new EffectSettingsViewModel();
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
        Assert.All(model.Effects, item => Assert.True(item.IsBuiltin));
        Assert.True(model.IsReadOnly);
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.DeleteCommand.CanExecute(null));
        Assert.True(model.ValidateCommand.CanExecute(null));
        Assert.True(model.ExportCommand.CanExecute(null));
        Assert.Null(typeof(EffectSettingsViewModel).GetProperty("DuplicateCommand"));
    }

    [Fact]
    public async Task NewScriptAppearsInTheListOnlyAfterPersistenceAcknowledgesTheSave()
    {
        var model = new EffectSettingsViewModel();
        EffectScriptPreset? submitted = null;
        model.SaveRequested += (_, args) => submitted = args.Preset;
        await model.AddCommand.ExecuteAsync(null);
        var draft = Assert.IsType<EffectScriptPreset>(model.Draft);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
        Assert.Null(model.SelectedEffect);
        Assert.Empty(model.SelectedIds);
        Assert.False(model.IsReadOnly);
        Assert.True(model.IsDirty);
        Assert.True(model.CanDelete);
        await model.SaveCommand.ExecuteAsync(null);
        Assert.Equal(draft, submitted);
        Assert.DoesNotContain(model.Effects, item => item.Id == draft.Id);
        Assert.True(model.IsDirty);

        model.UpdateEffects([submitted!]);

        Assert.Equal(BuiltinEffectScripts.Templates.Length + 1, model.Effects.Length);
        Assert.Equal(draft.Id, model.SelectedEffect!.Id);
        Assert.Equal(new[] { draft.Id }, model.SelectedIds.ToArray());
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task UnsavedInvalidScriptIsDeletedAfterConfirmationWithoutPersistenceOrValidation()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        model.Source = "effect broken";
        model.ValidateCommand.Execute(null);
        var error = model.Error;
        var deletes = 0;
        var saves = 0;
        model.DeleteRequested += (_, _) => deletes++;
        model.SaveRequested += (_, _) => saves++;
        Assert.True(model.DeleteCommand.CanExecute(null));

        model.DeleteCommand.Execute(null);

        Assert.Equal(1, deletes);
        Assert.Equal(0, saves);
        Assert.NotNull(model.Draft);
        Assert.Equal(error, model.Error);
        Assert.Equal("effect broken", model.Source);
        model.DiscardDraft();
        Assert.Null(model.Draft);
        Assert.False(model.IsDirty);
        Assert.Null(model.Error);
        Assert.Empty(model.Source);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
    }

    [Fact]
    public void SavedScriptWithAnInvalidPendingEditRequestsDeletionWithoutSaving()
    {
        var saved = Preset("saved-delete");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Source = "invalid draft";
        Guid? deleted = null;
        var saves = 0;
        model.DeleteRequested += (_, args) => deleted = args.Id;
        model.SaveRequested += (_, _) => saves++;

        model.DeleteCommand.Execute(null);

        Assert.Equal(saved.Id, deleted);
        Assert.Equal(0, saves);
        Assert.Null(model.Error);
        Assert.True(model.IsDirty);
        Assert.Contains(model.Effects, item => item.Id == saved.Id);
        model.UpdateEffects([]);
        Assert.DoesNotContain(model.Effects, item => item.Id == saved.Id);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void MultiSelectionExportsOnlyTheSavedSelectedSnapshotsAndDisablesEditingCommands()
    {
        var first = Preset("first");
        var second = Preset("second");
        var omitted = Preset("omitted");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([first, second, omitted]);
        var builtin = model.Effects.First(item => item.IsBuiltin);
        model.SelectEffects(second.Id, [builtin.Id, second.Id]);
        ImmutableArray<EffectScriptPreset> exported = [];
        model.ExportRequested += (_, args) => exported = args.Presets;
        Assert.True(model.ExportCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.DeleteCommand.CanExecute(null));
        Assert.False(model.ValidateCommand.CanExecute(null));
        Assert.True(model.IsReadOnly);
        Assert.Null(model.Draft);

        model.ExportCommand.Execute(null);

        Assert.Equal(new[] { builtin.Preset, second }, exported);
        model.RefreshLanguage();
        model.UpdateEffects([first, second, omitted]);
        Assert.Equal(new[] { builtin.Id, second.Id }, model.SelectedIds);
        model.SelectEffects(null, []);
        Assert.False(model.ExportCommand.CanExecute(null));
    }

    [Fact]
    public void ExportUsesTheSavedSnapshotEvenWhenItsEditorContainsAnInvalidDraft()
    {
        var saved = Preset("saved-export");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Source = "effect broken";
        model.ValidateCommand.Execute(null);
        var error = model.Error;
        var failures = 0;
        EffectScriptPreset? exported = null;
        model.ValidationFailed += (_, _) => failures++;
        model.ExportRequested += (_, args) => exported = Assert.Single(args.Presets);

        model.ExportCommand.Execute(null);

        Assert.Equal(saved, exported);
        Assert.Equal(0, failures);
        Assert.Equal(error, model.Error);
        Assert.Equal("effect broken", model.Source);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task IndependentNewDraftCannotBeExportedBeforeItIsSaved()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        var exports = 0;
        model.ExportRequested += (_, _) => exports++;
        model.ExportCommand.Execute(null);
        Assert.False(model.ExportCommand.CanExecute(null));
        Assert.Equal(0, exports);
    }

    [Fact]
    public void SuccessfulSaveRefreshClearsOnlyItsOwnSavedItemDraft()
    {
        var first = Preset("first-edit");
        var second = Preset("second-edit");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([first, second], first.Id);
        model.Source += "# first edit\n";
        var savedFirst = model.Draft!;
        model.SelectedEffect = model.Effects.Single(item => item.Id == second.Id);
        model.Source += "# second edit\n";
        var pendingSecond = model.Source;

        model.UpdateEffects([savedFirst, second], savedFirst.Id);

        Assert.False(model.IsDirty);
        model.SelectedEffect = model.Effects.Single(item => item.Id == second.Id);
        Assert.True(model.IsDirty);
        Assert.Equal(pendingSecond, model.Source);
    }

    [Fact]
    public async Task SavingRejectsReservedScriptIdsWithoutInsertingTheNewDraft()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        model.Source = BuiltinEffectScripts.Get("fade-in").Source;
        var saves = 0;
        model.SaveRequested += (_, _) => saves++;
        await model.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(model.Error);
        Assert.Equal(0, saves);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task SaveDecisionPersistsThePreviousDraftBeforeSelectionChanges()
    {
        var saved = Preset("selection-save");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Source += "# pending edit\n";
        var target = model.Effects.First(item => item.IsBuiltin);
        var saves = 0;
        var confirmations = 0;
        model.ConfirmLeaveAsync = () =>
        {
            confirmations++;
            return Task.FromResult(0);
        };
        model.SaveDraftAsync = pending =>
        {
            saves++;
            Assert.Equal(saved.Id, model.SelectedEffect!.Id);
            Assert.True(model.IsDirty);
            model.UpdateEffects([pending]);
            return Task.FromResult(true);
        };

        Assert.True(await model.SelectEffectsAsync(target.Id, [target.Id]));

        Assert.Equal(1, confirmations);
        Assert.Equal(1, saves);
        Assert.Equal(target.Id, model.SelectedEffect!.Id);
        Assert.False(model.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrFailedSaveKeepsTheOldSelectionAndDraft(bool invalid)
    {
        var saved = Preset("selection-failure");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Source = invalid ? "effect broken" : model.Source + "# pending\n";
        var source = model.Source;
        var target = model.Effects.First(item => item.IsBuiltin);
        var saves = 0;
        model.ConfirmLeaveAsync = () => Task.FromResult(0);
        model.SaveDraftAsync = _ =>
        {
            saves++;
            return Task.FromResult(false);
        };

        Assert.False(await model.SelectEffectsAsync(target.Id, [target.Id]));

        Assert.Equal(saved.Id, model.SelectedEffect!.Id);
        Assert.Equal(source, model.Source);
        Assert.True(model.IsDirty);
        Assert.Equal(invalid ? 0 : 1, saves);
        Assert.Equal(new[] { saved.Id }, model.SelectedIds);
        if (invalid)
        {
            Assert.NotNull(model.Error);
        }
    }

    [Fact]
    public async Task MissingSaveCallbackDoesNotAllowADirtyDraftToBeLost()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        var pending = model.Draft;
        var target = model.Effects[0];
        Assert.False(await model.SavePendingAsync());
        Assert.False(await model.SelectEffectsAsync(target.Id, [target.Id]));
        await model.AddCommand.ExecuteAsync(null);
        Assert.Equal(pending, model.Draft);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task DiscardRestoresTheSavedSnapshotAndCancelKeepsTheInvalidDraft(int decision, bool leave)
    {
        var saved = Preset("decision");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Source = "effect broken";
        var saves = 0;
        model.ConfirmLeaveAsync = () => Task.FromResult(decision);
        model.SaveDraftAsync = _ =>
        {
            saves++;
            return Task.FromResult(true);
        };

        Assert.Equal(leave, await model.PrepareToLeaveAsync());

        Assert.Equal(0, saves);
        Assert.Equal(leave ? saved.Source : "effect broken", model.Source);
        Assert.Equal(!leave, model.IsDirty);
        Assert.Equal(saved.Id, model.SelectedEffect!.Id);
    }

    [Fact]
    public async Task DiscardingANewDraftLeavesNoListEntryAndNoPendingSave()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        model.Source = "effect broken";
        model.ConfirmLeaveAsync = () => Task.FromResult(1);
        Assert.True(await model.PrepareToLeaveAsync());
        Assert.Null(model.Draft);
        Assert.Empty(model.SelectedIds);
        Assert.Equal(BuiltinEffectScripts.Templates.Length, model.Effects.Length);
        Assert.True(await model.SavePendingAsync());
    }

    [Fact]
    public async Task ConcurrentSaveRequestsShareOnePersistenceOperation()
    {
        var model = new EffectSettingsViewModel();
        await model.AddCommand.ExecuteAsync(null);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        model.SaveDraftAsync = _ =>
        {
            saves++;
            return release.Task;
        };
        var first = model.SavePendingAsync();
        var second = model.SavePendingAsync();
        Assert.Same(first, second);
        Assert.Equal(1, saves);
        release.SetResult(false);
        Assert.False(await first);
        Assert.True(model.IsDirty);
    }

    [Fact]
    public async Task PendingConfirmationBlocksConcurrentSelectionAndDoesNotAskTwice()
    {
        var saved = Preset("selection-race");
        var model = new EffectSettingsViewModel();
        model.UpdateEffects([saved], saved.Id);
        model.Name = "unsaved";
        var release = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirms = 0;
        model.ConfirmLeaveAsync = () =>
        {
            confirms++;
            return release.Task;
        };
        var firstTarget = model.Effects[0];
        var secondTarget = model.Effects[1];
        var first = model.SelectEffectsAsync(firstTarget.Id, [firstTarget.Id]);
        Assert.False(await model.SelectEffectsAsync(secondTarget.Id, [secondTarget.Id]));
        Assert.Same(first, model.SelectionCompletion);
        Assert.Equal(saved.Id, model.SelectedEffect!.Id);
        release.SetResult(2);
        Assert.False(await first);
        Assert.Equal(1, confirms);
        Assert.True(model.IsDirty);
    }

    private static EffectScriptPreset Preset(string scriptId)
    {
        return new(Guid.NewGuid(), scriptId, BuiltinEffectScripts.Get("fade-in").Source
            .Replace("fade-in", scriptId, StringComparison.Ordinal));
    }
}

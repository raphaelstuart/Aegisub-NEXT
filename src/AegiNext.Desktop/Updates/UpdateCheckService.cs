using AegiNext.Application.Tasks;

namespace AegiNext.Desktop.Updates;

internal sealed class UpdateCheckService : IAsyncDisposable
{
    private readonly Lock gate = new();
    private readonly AegiTaskService tasks;
    private readonly IUpdateReleaseSource source;
    private readonly string currentVersion;
    private readonly HashSet<UpdateCheckRequest> notifying = [];
    private UpdateCheckRequest? pending;
    private Task? disposeTask;
    private bool closing;

    internal UpdateCheckService(AegiTaskService tasks, IUpdateReleaseSource source, string currentVersion)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(currentVersion);
        this.tasks = tasks;
        this.source = source;
        this.currentVersion = currentVersion;
    }

    internal event EventHandler<UpdateCheckCompletedEventArgs>? Completed;

    internal Task<UpdateCheckResult> CheckAsync(UpdateCheckTrigger trigger, UpdateChannel channel)
    {
        if (!Enum.IsDefined(trigger) || !Enum.IsDefined(channel))
        {
            throw new ArgumentException("The update check trigger and channel must be supported values.");
        }
        UpdateCheckRequest request;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (pending is { } existing)
            {
                if (trigger == UpdateCheckTrigger.MANUAL)
                {
                    existing.Trigger = trigger;
                }
                return existing.Completion.Task;
            }
            request = new(trigger, channel);
            pending = request;
        }
        try
        {
            var handle = tasks.Submit(new CheckUpdatesTask(source, channel, currentVersion));
            bool cancel;
            lock (gate)
            {
                request.Handle = handle;
                cancel = closing;
            }
            if (cancel)
            {
                handle.RequestCancel();
            }
            _ = ObserveAsync(request, handle);
        }
        catch (Exception error)
        {
            Complete(request, new(UpdateCheckStatus.FAILED, currentVersion, Error: error));
        }
        return request.Completion.Task;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        UpdateCheckRequest[] requests;
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposeTask is not null)
            {
                return new(disposeTask);
            }
            closing = true;
            Completed = null;
            requests = pending is null ? [.. notifying] : [.. notifying, pending];
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposeTask = completion.Task;
        }
        _ = DisposeCoreAsync(requests, completion);
        return new(completion.Task);
    }

    private async Task ObserveAsync(UpdateCheckRequest request, AegiTaskHandle<UpdateCheckResult> handle)
    {
        UpdateCheckResult result;
        try
        {
            result = await handle.Completion;
        }
        catch (OperationCanceledException error)
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, request))
                {
                    pending = null;
                }
                request.Completion.TrySetCanceled(error.CancellationToken);
            }
            return;
        }
        catch (Exception error)
        {
            result = new(UpdateCheckStatus.FAILED, currentVersion, Error: error);
        }
        Complete(request, result);
    }

    private void Complete(UpdateCheckRequest request, UpdateCheckResult result)
    {
        EventHandler<UpdateCheckCompletedEventArgs>[] handlers;
        UpdateCheckCompletedEventArgs notification;
        lock (gate)
        {
            if (ReferenceEquals(pending, request))
            {
                pending = null;
            }
            if (closing)
            {
                request.Completion.TrySetCanceled();
                return;
            }
            notification = new(request.Trigger, request.Channel, result);
            handlers = Completed?.GetInvocationList().Cast<EventHandler<UpdateCheckCompletedEventArgs>>().ToArray() ?? [];
            notifying.Add(request);
        }
        foreach (var handler in handlers)
        {
            lock (gate)
            {
                if (closing)
                {
                    break;
                }
            }
            try
            {
                handler(this, notification);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("Update check result observer failed: {0}", error);
            }
        }
        lock (gate)
        {
            notifying.Remove(request);
            request.Completion.TrySetResult(result);
        }
    }

    private async Task DisposeCoreAsync(IReadOnlyList<UpdateCheckRequest> requests, TaskCompletionSource completion)
    {
        try
        {
            foreach (var request in requests)
            {
                AegiTaskHandle<UpdateCheckResult>? handle;
                lock (gate)
                {
                    handle = request.Handle;
                }
                handle?.RequestCancel();
            }
            try
            {
                await Task.WhenAll(requests.Select(request => request.Completion.Task)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }
}

using System.Diagnostics;
using AegiNext.Core.Timing;

namespace AegiNext.Desktop.Workspace;

internal sealed class PreviewInteractionDiagnostics
{
    private const int EVENT_CAPACITY = 512;
    private readonly Queue<PreviewInteractionEvent> events = new();
    private long session;
    private long sequence;
    private long acceptedSequence;
    private MediaTime? target;
    private MediaTime? acceptedTarget;

    internal bool Enabled { get; set; }
    internal event Action<PreviewInteractionEvent>? Recorded;
    internal IReadOnlyList<PreviewInteractionEvent> Events => events.ToArray();

    internal void Begin()
    {
        session++;
        target = acceptedTarget = null;
        acceptedSequence = 0;
        Record("begin");
    }

    internal void Input(MediaTime value)
    {
        sequence++;
        target = value;
        Record("input");
    }

    internal void Accept(MediaTime value)
    {
        acceptedSequence = sequence;
        acceptedTarget = value;
        Record("accepted");
    }

    internal void Record(string stage, MediaTime? frameTime = null, MediaTime? frameEnd = null)
    {
        if (!Enabled)
        {
            return;
        }

        var delivery = stage is "delivered" or "cached-delivered";
        var value = new PreviewInteractionEvent(stage, session, delivery ? acceptedSequence : sequence,
            delivery ? acceptedTarget : target, frameTime, frameEnd, Stopwatch.GetTimestamp());
        events.Enqueue(value);
        while (events.Count > EVENT_CAPACITY)
        {
            events.Dequeue();
        }
        Recorded?.Invoke(value);
    }
}

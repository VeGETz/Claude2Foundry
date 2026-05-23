using System.Text.Json;
using System.Threading.Channels;

namespace Claude2Foundry.Monitor;

public sealed class RequestCapturePipeline : IRequestCaptureSink, IAsyncDisposable
{
    private const int RingCapacity = 500;
    private const int ChannelCapacity = 1024;
    private const int SubscriberQueueCapacity = 256;

    private readonly Channel<CaptureEvent> _ingest = Channel.CreateBounded<CaptureEvent>(new BoundedChannelOptions(ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private readonly LinkedList<RingSummary> _ring = new();
    private readonly Dictionary<string, LinkedListNode<RingSummary>> _ringIndex = new();
    private readonly List<SubscriberSink> _subscribers = [];
    private readonly Lock _ringLock = new();
    private readonly Lock _subscriberLock = new();

    private readonly JsonlWriter _jsonlWriter;
    private readonly FullBodyCache _bodyCache;
    // Accumulates per-request bodies; only touched by the single-reader background task
    private readonly Dictionary<string, RequestFullRecord> _accum = new();
    private Task? _backgroundTask;
    private long _sequence;

    public int RingOccupancy { get { lock (_ringLock) return _ring.Count; } }
    public int RingCapacityMax => RingCapacity;

    public RequestCapturePipeline(JsonlWriter jsonlWriter, FullBodyCache bodyCache)
    {
        _jsonlWriter = jsonlWriter;
        _bodyCache = bodyCache;
    }

    public void Start(CancellationToken appStopping)
    {
        _backgroundTask = ProcessAsync(appStopping);
    }

    public void Emit(CaptureEvent evt)
    {
        _ingest.Writer.TryWrite(evt);
    }

    public void Finalize(string id, Exception? error = null)
    {
        if (error is not null)
            Emit(new CaptureErrorEvent(id, "unknown", "Adapter", error.Message));
    }

    public IAsyncEnumerable<SseFrame> SubscribeAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var sink = new SubscriberSink(SubscriberQueueCapacity);
        lock (_subscriberLock) _subscribers.Add(sink);

        // Emit replay snapshot immediately
        RingSummary[] snapshot;
        lock (_ringLock)
        {
            snapshot = since.HasValue
                ? [.. _ring.Where(r => r.Ts >= since.Value)]
                : [.. _ring];
        }

        var seq = Interlocked.Increment(ref _sequence);
        var replayJson = JsonSerializer.Serialize(new { records = snapshot });
        sink.TryEnqueue(new SseFrame(seq, "replay.snapshot", replayJson), terminal: false);

        return sink.ReadAllAsync(ct, onDispose: () =>
        {
            lock (_subscriberLock) _subscribers.Remove(sink);
        });
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        await foreach (var evt in _ingest.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try { HandleEvent(evt); }
            catch { /* swallow to keep pipeline alive */ }
        }
    }

    private void HandleEvent(CaptureEvent evt)
    {
        bool isTerminal = evt is FoundryCompleteEvent or ResponseSentEvent or CaptureErrorEvent;
        var (eventName, json) = SerializeEvent(evt);
        var seq = Interlocked.Increment(ref _sequence);
        var frame = new SseFrame(seq, eventName, json);

        UpdateRing(evt);
        AccumulateBody(evt);

        lock (_subscriberLock)
        {
            foreach (var sub in _subscribers)
                sub.TryEnqueue(frame, isTerminal);
        }
    }

    // Called only from the single-reader background task — no locking required.
    private void AccumulateBody(CaptureEvent evt)
    {
        switch (evt)
        {
            case RequestReceivedEvent e:
                _accum[e.Id] = new RequestFullRecord { Id = e.Id, Phase = "received", AnthropicBody = e.BodyPreview };
                break;

            case RequestTranslatedEvent e:
                if (_accum.TryGetValue(e.Id, out var cur1))
                    _accum[e.Id] = cur1 with { Phase = "translated", OpenaiBody = e.OpenaiBody };
                break;

            case ResponseSentEvent e:
                var partial = _accum.TryGetValue(e.Id, out var cur2)
                    ? cur2
                    : new RequestFullRecord { Id = e.Id, Phase = "complete" };
                var full = partial with { Phase = "complete", AnthropicAssembled = e.AnthropicAssembled };
                _bodyCache.Store(e.Id, full);
                _jsonlWriter.Write(e.Id, full);
                _accum.Remove(e.Id);
                break;

            case CaptureErrorEvent e:
                var errPartial = _accum.TryGetValue(e.Id, out var cur3)
                    ? cur3
                    : new RequestFullRecord { Id = e.Id, Phase = "error" };
                _bodyCache.Store(e.Id, errPartial with { Phase = "error" });
                _accum.Remove(e.Id);
                break;
        }
    }

    private void UpdateRing(CaptureEvent evt)
    {
        lock (_ringLock)
        {
            if (!_ringIndex.TryGetValue(evt.Id, out var node))
            {
                var summary = new RingSummary
                {
                    Id = evt.Id,
                    Ts = evt is RequestReceivedEvent rr ? rr.Ts : DateTimeOffset.UtcNow,
                    OriginalModel = evt is RequestReceivedEvent rrr ? rrr.OriginalModel : "unknown",
                    Phase = "received",
                    Status = "ok"
                };

                if (_ring.Count >= RingCapacity)
                {
                    var oldest = _ring.First!.Value;
                    _ring.RemoveFirst();
                    _ringIndex.Remove(oldest.Id);
                }

                node = _ring.AddLast(summary);
                _ringIndex[evt.Id] = node;
            }

            node.Value = UpdateSummary(node.Value, evt);
        }
    }

    private static RingSummary UpdateSummary(RingSummary s, CaptureEvent evt) => evt switch
    {
        RequestReceivedEvent e => s with { Ts = e.Ts, OriginalModel = e.OriginalModel, Phase = "received" },
        RequestTranslatedEvent e => s with { ResolvedModel = e.ResolvedModel, Phase = "translated" },
        FoundrySentEvent => s with { Phase = "foundry-sent" },
        FoundryChunkEvent => s with { Phase = "streaming" },
        FoundryCompleteEvent e => s with { Phase = "complete", Status = "ok", Usage = e.Usage },
        ResponseSentEvent e => s with { Phase = "complete", Status = "ok", ElapsedMs = e.ElapsedMs },
        CaptureErrorEvent e => s with { Phase = "error", Status = "error", Error = e.Message },
        _ => s
    };

    private static (string eventName, string json) SerializeEvent(CaptureEvent evt)
    {
        return evt switch
        {
            RequestReceivedEvent e => ("request.received", JsonSerializer.Serialize(new
            {
                id = e.Id, ts = e.Ts, originalModel = e.OriginalModel,
                stream = e.Stream, headers = e.Headers, bodyPreview = e.BodyPreview
            })),
            RequestTranslatedEvent e => ("request.translated", JsonSerializer.Serialize(new
            {
                id = e.Id, resolvedModel = e.ResolvedModel, openaiBody = e.OpenaiBody
            })),
            FoundrySentEvent e => ("foundry.request.sent", JsonSerializer.Serialize(new { id = e.Id, ts = e.Ts })),
            FoundryChunkEvent e => ("foundry.chunk", JsonSerializer.Serialize(new
            {
                id = e.Id, seq = e.Seq, deltaText = e.DeltaText,
                deltaToolCall = e.DeltaToolCall, reasoningDelta = e.ReasoningDelta
            })),
            FoundryCompleteEvent e => ("foundry.complete", JsonSerializer.Serialize(new
            {
                id = e.Id, usage = e.Usage, finishReason = e.FinishReason
            })),
            ResponseSentEvent e => ("response.sent", JsonSerializer.Serialize(new
            {
                id = e.Id, elapsedMs = e.ElapsedMs, anthropicAssembled = e.AnthropicAssembled
            })),
            CaptureErrorEvent e => ("error", JsonSerializer.Serialize(new
            {
                id = e.Id, phase = e.Phase, origin = e.Origin, message = e.Message
            })),
            _ => ("unknown", "{}")
        };
    }

    public async Task DrainAsync()
    {
        _ingest.Writer.TryComplete();
        if (_backgroundTask is not null)
            await _backgroundTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _ingest.Writer.TryComplete();
        if (_backgroundTask is not null)
            await _backgroundTask.ConfigureAwait(false);
    }
}

public sealed record SseFrame(long Seq, string Event, string Data);

internal sealed class SubscriberSink(int capacity)
{
    private readonly Channel<SseFrame> _queue = Channel.CreateBounded<SseFrame>(new BoundedChannelOptions(capacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleWriter = false,
        SingleReader = true
    });

    public void TryEnqueue(SseFrame frame, bool terminal)
    {
        if (terminal)
        {
            // Force-write: if queue is full, the oldest gets dropped (DropOldest mode)
            // then retry once to ensure terminal event lands
            _queue.Writer.TryWrite(frame);
        }
        else
        {
            _queue.Writer.TryWrite(frame);
        }
    }

    public async IAsyncEnumerable<SseFrame> ReadAllAsync(
        CancellationToken ct,
        Action? onDispose = null)
    {
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            onDispose?.Invoke();
        }
    }
}

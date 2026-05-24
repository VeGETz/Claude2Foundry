using System.Text.Json;
using System.Text.Json.Nodes;

namespace Claude2Foundry.Monitor;

public static class JsonlReader
{
    public static async Task<MonitorListResponse> ListAsync(
        string? filePath, int limit, string? before, CancellationToken ct = default)
    {
        if (filePath is null || !File.Exists(filePath))
            return new MonitorListResponse { Items = [] };

        var byId = new Dictionary<string, RequestAccumulator>();
        var order = new List<string>();

        await foreach (var node in ReadLinesAsync(filePath, ct))
        {
            var id = node["id"]?.GetValue<string>();
            if (id is null) continue;
            if (!byId.TryGetValue(id, out var acc))
            {
                acc = new RequestAccumulator(id);
                byId[id] = acc;
                order.Add(id);
            }
            acc.Consume(node);
        }

        // Newest-first
        var items = order
            .AsEnumerable()
            .Reverse()
            .Select(id => byId[id].ToListItem())
            .ToList();

        if (before is not null)
        {
            var idx = items.FindIndex(i => i.Id == before);
            if (idx >= 0) items = items.Skip(idx + 1).ToList();
        }

        return new MonitorListResponse { Items = items.Take(limit).ToList() };
    }

    public static async Task<MonitorDetail?> GetByIdAsync(
        string? filePath, string id, CancellationToken ct = default)
    {
        if (filePath is null || !File.Exists(filePath)) return null;

        var acc = new RequestAccumulator(id);
        bool found = false;
        await foreach (var node in ReadLinesAsync(filePath, ct))
        {
            if (node["id"]?.GetValue<string>() != id) continue;
            found = true;
            acc.Consume(node);
        }

        return found ? acc.ToDetail() : null;
    }

    private static async IAsyncEnumerable<JsonNode> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in File.ReadLinesAsync(filePath, ct))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }
            if (node is not null) yield return node;
        }
    }
}

public sealed record MonitorListResponse
{
    public required List<MonitorListItem> Items { get; init; }
}

internal sealed class RequestAccumulator(string id)
{
    private DateTimeOffset _ts = DateTimeOffset.MinValue;
    private string? _model;
    private string? _mappedModel;
    private string _status = "running";
    private int? _latencyMs;
    private int? _promptTokens;
    private int? _completionTokens;
    private JsonNode? _anthropicBody;
    private JsonNode? _openaiBody;
    private JsonNode? _openaiResponse;
    private JsonNode? _anthropicResponse;
    private JsonNode? _headers;
    private JsonNode? _error;

    public void Consume(JsonNode line)
    {
        var kind = line["kind"]?.GetValue<string>();
        var data = line["data"]?.AsObject();

        switch (kind)
        {
            case "request.received":
                _ts = ParseTs(line["ts"]?.GetValue<string>());
                _model = line["model"]?.GetValue<string>();
                _anthropicBody = data?["anthropicBody"]?.DeepClone();
                _headers = data?["headers"]?.DeepClone();
                break;

            case "request.translated":
                _mappedModel = data?["mappedModel"]?.GetValue<string>();
                _openaiBody = data?["openaiBody"]?.DeepClone();
                break;

            case "response.received":
                _openaiResponse = data?["openaiResponse"]?.DeepClone();
                _promptTokens = data?["promptTokens"]?.GetValue<int?>();
                _completionTokens = data?["completionTokens"]?.GetValue<int?>();
                break;

            case "response.sent":
                _status = "ok";
                _latencyMs = data?["elapsedMs"]?.GetValue<int?>();
                _anthropicResponse = data?["anthropicResponse"]?.DeepClone();
                break;

            case "request.error":
                _status = "error";
                _error = data?.DeepClone();
                break;
        }
    }

    public MonitorListItem ToListItem() => new()
    {
        Id = id,
        Ts = _ts == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : _ts,
        Model = _model,
        OriginalModel = _model,
        MappedModel = _mappedModel,
        Status = _status,
        LatencyMs = _latencyMs,
        PromptTokens = _promptTokens,
        CompletionTokens = _completionTokens
    };

    public MonitorDetail ToDetail() => new()
    {
        Id = id,
        Ts = _ts == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : _ts,
        Model = _model,
        Status = _status,
        LatencyMs = _latencyMs,
        AnthropicBody = _anthropicBody,
        OpenaiBody = _openaiBody,
        OpenaiResponse = _openaiResponse,
        AnthropicResponse = _anthropicResponse,
        Headers = _headers,
        Error = _error
    };

    private static DateTimeOffset ParseTs(string? s) =>
        DateTimeOffset.TryParse(s, out var v) ? v : DateTimeOffset.MinValue;
}

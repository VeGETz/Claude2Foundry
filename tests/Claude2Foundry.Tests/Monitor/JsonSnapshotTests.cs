using System.Text.Json;
using Claude2Foundry.Monitor;

namespace Claude2Foundry.Tests.Monitor;

public class JsonSnapshotTests
{
    [Fact]
    public void Take_Null_ReturnsNull()
    {
        var result = JsonSnapshot.Take(null);
        Assert.Null(result);
    }

    [Fact]
    public void Take_PlainObject_RoundTrips()
    {
        var obj = new { model = "claude-test", maxTokens = 100, stream = false };
        var snapshot = JsonSnapshot.Take(obj);
        Assert.NotNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("claude-test", json);
        Assert.Contains("100", json);
    }

    [Fact]
    public void Take_ObjectWithJsonElement_SurvivesSourceDisposal()
    {
        // Arrange: parse a document, grab a JsonElement from it, embed it in an object.
        JsonElement inputSchema;
        using (var doc = JsonDocument.Parse("{\"type\":\"object\",\"properties\":{\"q\":{\"type\":\"string\"}}}"))
        {
            inputSchema = doc.RootElement.Clone(); // start with a real JsonElement
        }

        // Simulate the worst case: the element came from a non-cloned parse (a live document
        // that gets disposed after the request). We re-parse without Clone() here.
        JsonElement liveElement;
        JsonDocument liveDoc = JsonDocument.Parse("{\"type\":\"object\",\"required\":[\"q\"]}");
        liveElement = liveDoc.RootElement; // NOT cloned — dies when liveDoc is disposed

        var dto = new DtoWithJsonElement { Name = "search", InputSchema = liveElement };

        // Act: snapshot BEFORE disposing the source document
        var snapshot = JsonSnapshot.Take(dto);

        // Dispose the source document — this would invalidate liveElement
        liveDoc.Dispose();

        // Assert: serialization of the snapshot must succeed (no InvalidOperationException)
        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("search", json);
        Assert.Contains("object", json);
    }

    [Fact]
    public void Take_List_RoundTrips()
    {
        var list = new List<object?> { new { seq = 1 }, new { seq = 2 } };
        var snapshot = JsonSnapshot.Take(list);
        Assert.NotNull(snapshot);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.Contains("seq", json);
    }

    private sealed class DtoWithJsonElement
    {
        public string Name { get; set; } = "";
        public JsonElement InputSchema { get; set; }
    }
}

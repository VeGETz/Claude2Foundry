using Claude2Foundry.Monitor;

namespace Claude2Foundry.Tests.Monitor;

public class FullBodyCacheTests
{
    private static RequestFullRecord MakeRecord(string id) => new()
    {
        Id = id,
        Phase = "complete"
    };

    [Fact]
    public void Store_And_Get_Returns_Record()
    {
        var cache = new FullBodyCache();
        var record = MakeRecord("abc");
        cache.Store("abc", record);
        Assert.Equal(record, cache.Get("abc"));
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var cache = new FullBodyCache();
        Assert.Null(cache.Get("unknown"));
    }

    [Fact]
    public void Evicts_Oldest_When_Full()
    {
        var cache = new FullBodyCache();
        // Fill to capacity (100) + 1
        for (int i = 0; i < 100; i++)
            cache.Store($"id-{i}", MakeRecord($"id-{i}"));

        // id-0 should still be present
        Assert.NotNull(cache.Get("id-0"));

        // Add one more — id-0 should be evicted
        cache.Store("id-100", MakeRecord("id-100"));
        Assert.Null(cache.Get("id-0"));
        Assert.NotNull(cache.Get("id-100"));
    }

    [Fact]
    public void Store_Duplicate_Updates_Without_Growing()
    {
        var cache = new FullBodyCache();
        var r1 = MakeRecord("abc");
        var r2 = new RequestFullRecord { Id = "abc", Phase = "updated" };

        cache.Store("abc", r1);
        cache.Store("abc", r2);

        Assert.Equal("updated", cache.Get("abc")!.Phase);
    }
}

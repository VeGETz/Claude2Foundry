using System.Runtime.CompilerServices;

namespace Claude2Foundry.Monitor;

public sealed class FullBodyCache
{
    private const int Capacity = 100;
    private readonly Dictionary<string, RequestFullRecord> _cache = new(Capacity + 1);
    private readonly LinkedList<string> _order = new();
    private readonly Lock _lock = new();

    public void Store(string id, RequestFullRecord record)
    {
        lock (_lock)
        {
            if (_cache.ContainsKey(id))
            {
                _order.Remove(id);
                _order.AddLast(id);
                _cache[id] = record;
                return;
            }

            if (_cache.Count >= Capacity)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _cache.Remove(oldest);
            }

            _cache[id] = record;
            _order.AddLast(id);
        }
    }

    public RequestFullRecord? Get(string id)
    {
        lock (_lock)
        {
            return _cache.GetValueOrDefault(id);
        }
    }
}

namespace Claude2Foundry.Monitor;

public interface IRequestCaptureSink
{
    void Emit(CaptureEvent evt);
    void Finalize(string id, Exception? error = null);
}

public sealed class NullCaptureSink : IRequestCaptureSink
{
    public static readonly NullCaptureSink Instance = new();
    public void Emit(CaptureEvent evt) { }
    public void Finalize(string id, Exception? error = null) { }
}

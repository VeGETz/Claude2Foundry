namespace Claude2Foundry.Admin;

public interface IExitSink
{
    void Exit(int code);
}

internal sealed class EnvironmentExitSink : IExitSink
{
    public void Exit(int code) => Environment.Exit(code);
}

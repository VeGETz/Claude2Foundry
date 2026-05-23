using Claude2Foundry.Admin;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claude2Foundry.Tests.Admin;

public class RestartCoordinatorTests
{
    [Fact]
    public void WrapperPresent_FalseByDefault()
    {
        var saved = Environment.GetEnvironmentVariable("C2F_WRAPPER");
        try
        {
            Environment.SetEnvironmentVariable("C2F_WRAPPER", null);
            var coord = new RestartCoordinator();
            Assert.False(coord.WrapperPresent);
        }
        finally { Environment.SetEnvironmentVariable("C2F_WRAPPER", saved); }
    }

    [Fact]
    public void WrapperPresent_TrueWhenEnvSet()
    {
        var saved = Environment.GetEnvironmentVariable("C2F_WRAPPER");
        try
        {
            Environment.SetEnvironmentVariable("C2F_WRAPPER", "1");
            var coord = new RestartCoordinator();
            Assert.True(coord.WrapperPresent);
        }
        finally { Environment.SetEnvironmentVariable("C2F_WRAPPER", saved); }
    }

    [Fact]
    public async Task InitiateRestart_ThrowsWhenNoWrapper()
    {
        var saved = Environment.GetEnvironmentVariable("C2F_WRAPPER");
        try
        {
            Environment.SetEnvironmentVariable("C2F_WRAPPER", null);
            var coord = new RestartCoordinator();
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await coord.InitiateRestartAsync(NullLogger.Instance));
        }
        finally { Environment.SetEnvironmentVariable("C2F_WRAPPER", saved); }
    }

    [Fact]
    public void TrackRequest_IncrementsAndDecrements()
    {
        var coord = new RestartCoordinator();
        Assert.Equal(0, coord.InFlightCount);

        using (coord.TrackRequest())
        {
            Assert.Equal(1, coord.InFlightCount);
            using (coord.TrackRequest())
                Assert.Equal(2, coord.InFlightCount);
            Assert.Equal(1, coord.InFlightCount);
        }

        Assert.Equal(0, coord.InFlightCount);
    }
}

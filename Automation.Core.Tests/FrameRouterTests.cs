using Automation.Core.Dispatching;
using Xunit;

namespace Automation.Core.Tests;

public sealed class FrameRouterTests
{
    [Fact]
    public async Task DispatchExecutesOnlyHighestPriorityMatchingRoute()
    {
        var calls = new List<string>();
        var router = new FrameRouter<string>(
        [
            new FrameRoute<string>(
                "low",
                10,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("low");
                    return ValueTask.FromResult(true);
                }),
            new FrameRoute<string>(
                "high",
                0,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("high");
                    return ValueTask.FromResult(true);
                })
        ]);

        var result = await router.DispatchAsync("route", CancellationToken.None);

        Assert.True(result.Matched);
        Assert.Equal(["high"], calls);
    }

    [Fact]
    public async Task DispatchStopsAfterFirstMatchEvenWhenHandlerDeclines()
    {
        var calls = new List<string>();
        var router = new FrameRouter<string>(
        [
            new FrameRoute<string>(
                "high",
                0,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("high");
                    return ValueTask.FromResult(false);
                }),
            new FrameRoute<string>(
                "low",
                10,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("low");
                    return ValueTask.FromResult(true);
                })
        ]);

        var result = await router.DispatchAsync("route", CancellationToken.None);

        Assert.True(result.Matched);
        Assert.False(result.Handled);
        Assert.Equal(["high"], calls);
    }
    [Fact]
    public async Task DispatchContinuesToNextMatchingRouteWhenFallbackIsAllowed()
    {
        var calls = new List<string>();
        var router = new FrameRouter<string>(
        [
            new FrameRoute<string>(
                "high",
                0,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("high");
                    return ValueTask.FromResult(false);
                },
                AllowFallbackWhenUnhandled: true),
            new FrameRoute<string>(
                "low",
                10,
                frame => frame == "route",
                (_, _) =>
                {
                    calls.Add("low");
                    return ValueTask.FromResult(true);
                })
        ]);

        var result = await router.DispatchAsync("route", CancellationToken.None);

        Assert.True(result.Matched);
        Assert.True(result.Handled);
        Assert.Equal(["high", "low"], calls);
    }

    [Fact]
    public async Task DispatchReturnsFalseWhenNoRouteMatches()
    {
        var invoked = false;
        var router = new FrameRouter<string>(
        [
            new FrameRoute<string>(
                "route",
                0,
                frame => frame == "matched",
                (_, _) =>
                {
                    invoked = true;
                    return ValueTask.FromResult(true);
                })
        ]);

        var result = await router.DispatchAsync("unknown", CancellationToken.None);

        Assert.False(result.Matched);
        Assert.False(invoked);
    }
}
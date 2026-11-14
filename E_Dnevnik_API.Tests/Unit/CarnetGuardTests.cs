using System.Net;
using System.Net.Http.Headers;
using E_Dnevnik_API.ScrapingServices;

namespace E_Dnevnik_API.Tests.Unit;

public class CarnetGuardTests
{
    // sat koji pomičemo ručno; Task.Delay/timeri i dalje idu stvarnim vremenom
    private class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Calls++;
            return Respond(request, cancellationToken);
        }
    }

    private static CarnetGuardOptions NoRateLimit(Action<CarnetGuardOptions>? tweak = null)
    {
        var o = new CarnetGuardOptions { MaxRequestsPerSecond = 0 };
        tweak?.Invoke(o);
        return o;
    }

    private static (HttpClient client, StubHandler stub) Client(
        CarnetGuard guard,
        TimeSpan? timeout = null
    )
    {
        var stub = new StubHandler();
        var client = new HttpClient(
            new CarnetGuardHandler(guard, timeout ?? TimeSpan.FromSeconds(30)) { InnerHandler = stub }
        );
        return (client, stub);
    }

    private static HttpResponseMessage Status(int code) => new((HttpStatusCode)code);

    private const string Url = "https://ocjene.skole.hr/course";

    // --- kill switch ---

    [Fact]
    public async Task KillSwitch_BlocksEveryRequest_WithoutSending()
    {
        var stats = new CarnetRequestStats();
        var guard = new CarnetGuard(NoRateLimit(o => o.Enabled = false), stats: stats);
        var (client, stub) = Client(guard);

        var ex = await Assert.ThrowsAsync<CarnetPausedException>(() => client.GetAsync(Url));

        Assert.Equal(CarnetPauseReason.Disabled, ex.Reason);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(0, stub.Calls);
        Assert.Equal(1, stats.Snapshot().Blocked);
        Assert.NotNull(guard.PausedFor());
    }

    [Fact]
    public void PausedException_IsBackoffForBackgroundJob()
    {
        Assert.True(
            CarnetBackoff.ShouldBackOff(new CarnetPausedException(CarnetPauseReason.CircuitOpen, TimeSpan.Zero))
        );
    }

    // --- globalni limit ---

    [Fact]
    public void RateLimit_SpacesRequestsAtOneOverRps()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(new CarnetGuardOptions { MaxRequestsPerSecond = 4 }, time);

        Assert.Equal(TimeSpan.Zero, guard.TryReserve(time.Now));
        Assert.Equal(TimeSpan.FromMilliseconds(250), guard.TryReserve(time.Now));
        Assert.Equal(TimeSpan.FromMilliseconds(500), guard.TryReserve(time.Now));

        // nakon što prođe vrijeme, red je prazan
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.Zero, guard.TryReserve(time.Now));
    }

    [Fact]
    public void RateLimit_RejectsWhenQueueWouldWaitTooLong()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(
            new CarnetGuardOptions { MaxRequestsPerSecond = 1, MaxQueueWait = TimeSpan.FromSeconds(2) },
            time
        );

        Assert.NotNull(guard.TryReserve(time.Now)); // 0 s
        Assert.NotNull(guard.TryReserve(time.Now)); // 1 s
        Assert.NotNull(guard.TryReserve(time.Now)); // 2 s
        Assert.Null(guard.TryReserve(time.Now)); // 3 s > 2 s
    }

    [Fact]
    public async Task RateLimit_FullQueue_ThrowsOverloaded()
    {
        var time = new ManualTime();
        var stats = new CarnetRequestStats();
        var guard = new CarnetGuard(
            new CarnetGuardOptions { MaxRequestsPerSecond = 0.1, MaxQueueWait = TimeSpan.Zero },
            time,
            stats
        );
        var (client, stub) = Client(guard);

        await client.GetAsync(Url);
        var ex = await Assert.ThrowsAsync<CarnetPausedException>(() => client.GetAsync(Url));

        Assert.Equal(CarnetPauseReason.Overloaded, ex.Reason);
        Assert.Equal(1, stub.Calls);
    }

    // --- circuit breaker ---

    [Fact]
    public async Task Breaker_OpensAfterThresholdFailures_ThenBlocks()
    {
        var time = new ManualTime();
        var stats = new CarnetRequestStats();
        var guard = new CarnetGuard(NoRateLimit(), time, stats);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(502));

        for (var i = 0; i < 3; i++)
            await client.GetAsync(Url); // 5xx se vraća pozivatelju kao i prije

        var ex = await Assert.ThrowsAsync<CarnetPausedException>(() => client.GetAsync(Url));
        Assert.Equal(CarnetPauseReason.CircuitOpen, ex.Reason);
        Assert.Equal(TimeSpan.FromMinutes(5), ex.RetryAfter);
        Assert.Equal(3, stub.Calls);
        Assert.Equal(1, stats.Snapshot().BreakerOpens);
    }

    [Fact]
    public async Task Breaker_FailuresOutsideWindow_DoNotOpen()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(500));

        await client.GetAsync(Url);
        await client.GetAsync(Url);
        time.Advance(TimeSpan.FromSeconds(61));
        await client.GetAsync(Url);

        Assert.Null(guard.PausedFor());
    }

    [Fact]
    public async Task Breaker_4xxAndRedirects_AreNotFailures()
    {
        var guard = new CarnetGuard(NoRateLimit());
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(404));

        for (var i = 0; i < 5; i++)
            await client.GetAsync(Url);

        Assert.Null(guard.PausedFor());
    }

    [Fact]
    public async Task Breaker_429_OpensImmediately_AndHonoursRetryAfter()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) =>
        {
            var r = Status(429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return Task.FromResult(r);
        };

        await client.GetAsync(Url);

        Assert.Equal(TimeSpan.FromMinutes(20), guard.PausedFor());
    }

    [Fact]
    public async Task Breaker_RetryAfter_IsCappedAtMaxPause()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) =>
        {
            var r = Status(429);
            r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(1));
            return Task.FromResult(r);
        };

        await client.GetAsync(Url);

        Assert.Equal(TimeSpan.FromHours(1), guard.PausedFor());
    }

    [Fact]
    public async Task Breaker_HalfOpen_SuccessfulTrialCloses()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(429));
        await client.GetAsync(Url);

        time.Advance(TimeSpan.FromMinutes(5));
        stub.Respond = (_, _) => Task.FromResult(Status(200));
        await client.GetAsync(Url); // probni
        await client.GetAsync(Url); // opet normalno

        Assert.Null(guard.PausedFor());
        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task Breaker_HalfOpen_FailedTrialReopensWithDoublePause()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(503));
        for (var i = 0; i < 3; i++)
            await client.GetAsync(Url);

        time.Advance(TimeSpan.FromMinutes(5));
        await client.GetAsync(Url); // probni pada

        Assert.Equal(TimeSpan.FromMinutes(10), guard.PausedFor());
    }

    [Fact]
    public async Task Breaker_HalfOpen_OnlyOneTrialAtATime()
    {
        var time = new ManualTime();
        var guard = new CarnetGuard(NoRateLimit(), time);
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => Task.FromResult(Status(429));
        await client.GetAsync(Url);
        time.Advance(TimeSpan.FromMinutes(5));

        var release = new TaskCompletionSource<HttpResponseMessage>();
        stub.Respond = (_, _) => release.Task;
        var trial = client.GetAsync(Url);

        var ex = await Assert.ThrowsAsync<CarnetPausedException>(() => client.GetAsync(Url));
        Assert.Equal(CarnetPauseReason.CircuitOpen, ex.Reason);

        release.SetResult(Status(200));
        await trial;
        Assert.Null(guard.PausedFor());
    }

    // --- timeout ---

    [Fact]
    public async Task Timeout_CountsAsFailure_AndThrowsTaskCanceled()
    {
        var guard = new CarnetGuard(NoRateLimit(o => o.FailureThreshold = 1));
        var (client, stub) = Client(guard, TimeSpan.FromMilliseconds(50));
        stub.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Status(200);
        };

        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetAsync(Url));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.True(CarnetBackoff.ShouldBackOff(ex));
        Assert.NotNull(guard.PausedFor());
    }

    [Fact]
    public async Task CallerCancel_IsNotAFailure()
    {
        var guard = new CarnetGuard(NoRateLimit(o => o.FailureThreshold = 1));
        var (client, stub) = Client(guard);
        stub.Respond = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Status(200);
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(Url, cts.Token));

        Assert.Null(guard.PausedFor());
    }

    [Fact]
    public async Task NetworkError_CountsAsFailure()
    {
        var guard = new CarnetGuard(NoRateLimit(o => o.FailureThreshold = 1));
        var (client, stub) = Client(guard);
        stub.Respond = (_, _) => throw new HttpRequestException("connection refused");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Url));

        Assert.NotNull(guard.PausedFor());
    }

    // --- CarnetHttp lanac ---

    [Fact]
    public async Task CreateClient_BlockedRequests_AreNotCountedAsSent()
    {
        var stats = new CarnetRequestStats();
        var guard = new CarnetGuard(NoRateLimit(o => o.Enabled = false), stats: stats);
        using var client = CarnetHttp.CreateClient(
            new CookieContainer(),
            CarnetRequestSource.UserScrape,
            "a@test.com",
            stats: stats,
            guard: guard
        );

        await Assert.ThrowsAsync<CarnetPausedException>(() => client.GetAsync(Url));

        Assert.Equal(0, stats.Snapshot().Total);
        Assert.Equal(1, stats.Snapshot().Blocked);
    }

    [Fact]
    public void RetryAfterSeconds_RoundsUpAndIsAtLeastOne()
    {
        Assert.Equal(1, new CarnetPausedException(CarnetPauseReason.CircuitOpen, TimeSpan.Zero).RetryAfterSeconds);
        Assert.Equal(2, new CarnetPausedException(CarnetPauseReason.CircuitOpen, TimeSpan.FromSeconds(1.2)).RetryAfterSeconds);
    }
}

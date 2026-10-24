using System.Net;
using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.Services;

namespace E_Dnevnik_API.Tests.Unit;

public class CarnetGoodCitizenTests
{
    private class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    // --- User-Agent ---

    [Fact]
    public void CreateClient_SetsOdlikasUserAgent()
    {
        using var client = CarnetHttp.CreateClient(
            new CookieContainer(),
            CarnetRequestSource.UserScrape,
            null
        );

        Assert.Equal(CarnetHttp.UserAgent, client.DefaultRequestHeaders.UserAgent.ToString());
        Assert.StartsWith("Odlikas/", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public void CreateClient_UsesDefaultTimeout_UnlessOverridden()
    {
        using var def = CarnetHttp.CreateClient(new CookieContainer(), CarnetRequestSource.Login, null);
        using var custom = CarnetHttp.CreateClient(
            new CookieContainer(),
            CarnetRequestSource.Background,
            null,
            TimeSpan.FromSeconds(60)
        );

        Assert.Equal(CarnetHttp.DefaultTimeout, def.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(60), custom.Timeout);
    }

    // --- Request counting ---

    [Fact]
    public async Task CountingHandler_RecordsEachRequestBySource()
    {
        var stats = new CarnetRequestStats();
        using var bg = new HttpClient(
            new CarnetRequestCountingHandler(stats, CarnetRequestSource.Background, "a@test.com")
            {
                InnerHandler = new OkHandler(),
            }
        );
        using var login = new HttpClient(
            new CarnetRequestCountingHandler(stats, CarnetRequestSource.Login, "b@test.com")
            {
                InnerHandler = new OkHandler(),
            }
        );

        await bg.GetAsync("https://ocjene.skole.hr/course");
        await bg.GetAsync("https://ocjene.skole.hr/course");
        await login.GetAsync("https://ocjene.skole.hr/login");

        var s = stats.Snapshot();
        Assert.Equal(2, s.Background);
        Assert.Equal(1, s.Login);
        Assert.Equal(0, s.UserScrape);
        Assert.Equal(3, s.Total);
        Assert.Equal(2, s.UniqueUsers);
        Assert.Equal(1.5, s.RequestsPerUser);
    }

    [Fact]
    public void Stats_SameEmailDifferentCase_CountsAsOneUser()
    {
        var stats = new CarnetRequestStats();
        stats.RecordRequest(CarnetRequestSource.UserScrape, "Ivan@Skole.hr");
        stats.RecordRequest(CarnetRequestSource.UserScrape, "ivan@skole.hr");

        Assert.Equal(1, stats.Snapshot().UniqueUsers);
    }

    [Fact]
    public void Stats_SnapshotAndReset_ClearsCounters()
    {
        var stats = new CarnetRequestStats();
        stats.RecordRequest(CarnetRequestSource.Login, "a@test.com");
        stats.RecordCacheHit();

        var first = stats.SnapshotAndReset();
        var second = stats.Snapshot();

        Assert.Equal(1, first.Total);
        Assert.Equal(1, first.CacheHits);
        Assert.Equal(new CarnetRequestSnapshot(0, 0, 0, 0, 0), second);
        Assert.Equal(0, second.RequestsPerUser);
    }

    // --- Backoff ---

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Backoff_ScraperException_DependsOnStatus(int status, bool expected)
    {
        Assert.Equal(expected, CarnetBackoff.ShouldBackOff(new ScraperException(status, "x")));
    }

    [Fact]
    public void Backoff_TimeoutAndNetworkErrors_BackOff()
    {
        Assert.True(CarnetBackoff.ShouldBackOff(new TaskCanceledException()));
        Assert.True(CarnetBackoff.ShouldBackOff(new TimeoutException()));
        Assert.True(CarnetBackoff.ShouldBackOff(new HttpRequestException("dns")));
        Assert.True(
            CarnetBackoff.ShouldBackOff(
                new HttpRequestException("x", null, HttpStatusCode.TooManyRequests)
            )
        );
        Assert.False(
            CarnetBackoff.ShouldBackOff(new HttpRequestException("x", null, HttpStatusCode.Forbidden))
        );
    }

    [Fact]
    public void Backoff_UnrelatedException_DoesNotBackOff()
    {
        Assert.False(CarnetBackoff.ShouldBackOff(new InvalidOperationException()));
    }

    // --- Daily summary scheduling ---

    [Fact]
    public void UntilNextMidnight_Zagreb_SummerTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
        // 21:30 UTC = 23:30 CEST
        var delay = CarnetRequestStatsReporter.UntilNextMidnight(
            new DateTime(2026, 10, 24, 21, 30, 0, DateTimeKind.Utc),
            zone
        );
        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public void UntilNextMidnight_Zagreb_AcrossDstChange()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
        // 22:30 UTC 24.10. = 00:30 CEST 25.10.; sat se vraća taj dan, iduća ponoć je 23:00 UTC 25.10.
        var delay = CarnetRequestStatsReporter.UntilNextMidnight(
            new DateTime(2026, 10, 24, 22, 30, 0, DateTimeKind.Utc),
            zone
        );
        Assert.Equal(TimeSpan.FromHours(24.5), delay);
    }
}

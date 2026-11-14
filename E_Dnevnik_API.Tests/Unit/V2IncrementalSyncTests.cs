using System.Net;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.Models.ScrapeTests;
using E_Dnevnik_API.V2.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

// inkrementalni /v2/sync: stranice ocjena samo kad treba, raspored tjedno
public class V2IncrementalSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Caller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private static SubjectInfo S(string id, string avg, string name = "Predmet") => new(name + id, "Prof", avg, id);

    private static readonly List<SubjectInfo> Five = new() { S("1", "4,50"), S("2", "5,00"), S("3", "3,20"), S("4", "4,00"), S("5", "2,75") };

    private static Dictionary<string, SubjectFetchState> Seen(params (string Id, string Avg, double HoursAgo)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new SubjectFetchState(r.Avg, Now.AddHours(-r.HoursAgo)));

    // --- izbor stranica ---

    [Fact]
    public void Pick_FirstSync_FetchesEverySubject()
    {
        var pick = SyncFetchPlan.PickGradePages(Five, new Dictionary<string, SubjectFetchState>(), Now, full: false);
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, pick.OrderBy(x => x));
    }

    [Fact]
    public void Pick_NothingChanged_OnlyRotatesTheTwoStalest()
    {
        var state = Seen(("1", "4,50", 1), ("2", "5,00", 30), ("3", "3,20", 5), ("4", "4,00", 40), ("5", "2,75", 2));
        var pick = SyncFetchPlan.PickGradePages(Five, state, Now, full: false);
        Assert.Equal(new[] { "2", "4" }, pick.OrderBy(x => x));
    }

    [Fact]
    public void Pick_ChangedAverage_IsFetched_PlusRotation()
    {
        var state = Seen(("1", "4,00", 1), ("2", "5,00", 30), ("3", "3,20", 5), ("4", "4,00", 40), ("5", "2,75", 2));
        var pick = SyncFetchPlan.PickGradePages(Five, state, Now, full: false);
        Assert.Equal(new[] { "1", "2", "4" }, pick.OrderBy(x => x));
    }

    [Fact]
    public void Pick_StaleSubject_IsAlwaysFetched()
    {
        var state = Seen(("1", "4,50", 73), ("2", "5,00", 80), ("3", "3,20", 90), ("4", "4,00", 1), ("5", "2,75", 2));
        var pick = SyncFetchPlan.PickGradePages(Five, state, Now, full: false);
        // tri starija od 3 dana + dva najstarija od ostalih
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, pick.OrderBy(x => x));
    }

    [Fact]
    public void Pick_NeverFetchedRow_IsFetched()
    {
        var state = Seen(("1", "4,50", 1), ("2", "5,00", 1), ("3", "3,20", 1), ("4", "4,00", 1), ("5", "2,75", 1));
        state["3"] = new SubjectFetchState("3,20", null);
        var pick = SyncFetchPlan.PickGradePages(Five, state, Now, full: false);
        Assert.Contains("3", pick);
        Assert.Equal(3, pick.Count);
    }

    [Fact]
    public void Pick_Full_FetchesEverything()
    {
        var state = Seen(("1", "4,50", 1), ("2", "5,00", 1), ("3", "3,20", 1), ("4", "4,00", 1), ("5", "2,75", 1));
        Assert.Equal(5, SyncFetchPlan.PickGradePages(Five, state, Now, full: true).Count);
    }

    [Fact]
    public void Pick_SkipsHomeroomEmptyIdsAndDuplicates()
    {
        var list = new List<SubjectInfo>
        {
            new("Sat razrednika", "Ana", "", "9"),
            new("Matematika", "Ivo", "4,00", ""),
            S("1", "4,50"),
            S("1", "4,50"),
        };
        var pick = SyncFetchPlan.PickGradePages(list, new Dictionary<string, SubjectFetchState>(), Now, full: false);
        Assert.Equal(new[] { "1" }, pick);
    }

    [Fact]
    public void Pick_AverageComparisonIgnoresWhitespace()
    {
        var state = Seen(("1", " 4,50 ", 1));
        var pick = SyncFetchPlan.PickGradePages(new[] { S("1", "4,50") }, state, Now, full: false);
        // jedini predmet: ide samo zbog rotacije, ne zbog "promjene"
        Assert.Equal(new[] { "1" }, pick);
        var none = SyncFetchPlan.PickGradePages(
            new[] { S("1", "4,50"), S("2", "1,00"), S("3", "2,00") },
            Seen(("1", " 4,50 ", 1), ("2", "1,00", 2), ("3", "2,00", 3)),
            Now,
            full: false
        );
        Assert.DoesNotContain("1", none);
    }

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(24.0, false, false)]
    [InlineData(24.0 * 7, false, true)]
    [InlineData(1.0, true, true)]
    public void Schedule_FetchedWeeklyOrWhenFull(double? hoursAgo, bool full, bool expected)
    {
        DateTimeOffset? last = hoursAgo is { } h ? Now.AddHours(-h) : null;
        Assert.Equal(expected, SyncFetchPlan.ShouldFetchSchedule(last, Now, full));
    }

    // --- plan za bazu ---

    private static readonly List<SubjectRow> Seed = new()
    {
        new(1, "MAT", "Matematika"),
        new(3, "FIZ", "Fizika"),
    };

    private static string Page(string value) =>
        "<html><body><div class=\"row \" data-action-id=\"1\"><div data-date=\"10.10.2026.\" class=\"cell\"></div>"
        + $"<div class=\"box\"><div class=\"cell\"><span>{value}</span></div><div class=\"cell\"></div></div></div></body></html>";

    [Fact]
    public async Task Plan_UnfetchedSubject_KeepsRowWithoutGrades_AndUsesStoredGradesForAverage()
    {
        var snapshot = new EdnevnikSnapshot(
            null,
            new List<ScrapedSubject>
            {
                new(new SubjectInfo("Matematika", "Marko", "4,00", "100"), Page("4")),
                new(new SubjectInfo("Fizika", "Ivo", "2,00", "300"), null),
            },
            new List<TestInfo>()
        );
        var resolver = new SubjectResolver(new SubjectMapper(Seed), (_, _) => Task.FromResult((short)40));
        var stored = new Dictionary<short, List<short>> { [3] = new() { 2, 2 } };

        var plan = await SyncService.PlanAsync(Child, snapshot, resolver, new SchoolYear(2026), new DateOnly(2026, 11, 14), stored);

        var mat = plan.ChildSubjects.Single(c => c.SubjectId == 1);
        var fiz = plan.ChildSubjects.Single(c => c.SubjectId == 3);
        Assert.True(mat.GradesFetched);
        Assert.Equal("4,00", mat.Average);
        Assert.False(fiz.GradesFetched);
        Assert.Equal("300", fiz.EdnevnikRef);
        Assert.All(plan.Grades, g => Assert.Equal((short)1, g.SubjectId));
        Assert.Equal(2, plan.Response.SubjectCount);
        Assert.Equal("Fizika", plan.Response.WeakestSubject);
        Assert.Equal(2.0, plan.Response.WeakestAverage);
    }

    // --- cijeli sync ---

    private class Repo : ISyncRepository
    {
        public Dictionary<string, SubjectFetchState> State = new();
        public DateTimeOffset? LastSchedule;
        public SyncWrite? Written;
        public bool AskedForStoredGrades;

        public bool IsConfigured => true;

        public Task<ChildRecord?> GetChildAsync(Guid childId, CancellationToken ct) =>
            Task.FromResult<ChildRecord?>(new ChildRecord(childId, Guid.NewGuid(), Caller, SelfManaged: true));

        public Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct) => Task.FromResult(false);

        public Task<List<SubjectRow>> GetSubjectsAsync(CancellationToken ct) => Task.FromResult(Seed);

        public Task<Dictionary<string, SubjectFetchState>> GetFetchStateAsync(Guid childId, string schoolYear, CancellationToken ct) =>
            Task.FromResult(State);

        public Task<Dictionary<short, List<short>>> GetGradeValuesAsync(Guid childId, SchoolYear year, CancellationToken ct)
        {
            AskedForStoredGrades = true;
            return Task.FromResult(new Dictionary<short, List<short>>());
        }

        public Task<DateTimeOffset?> LastScheduleFetchAsync(Guid childId, CancellationToken ct) => Task.FromResult(LastSchedule);

        public Task<short> EdnevnikSubjectIdAsync(string ednevnikName, CancellationToken ct) => Task.FromResult((short)40);

        public Task<Guid> StartRunAsync(Guid childId, Guid familyId, Guid actorId, string? version, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());

        public Task<int> WriteAsync(SyncWrite write, CancellationToken ct)
        {
            Written = write;
            return Task.FromResult(0);
        }

        public Task FinishRunAsync(Guid runId, string status, int changed, string? errorCode, CancellationToken ct) =>
            Task.CompletedTask;
    }

    // radi kao pravi scraper: pita options koje stranice skinuti
    private class Scraper : IEdnevnikSyncScraper
    {
        public SyncFetchOptions? Options;

        public Task<EdnevnikSnapshot> ScrapeAsync(
            IReadOnlyList<SyncCookie> cookies,
            string userKey,
            SyncFetchOptions options,
            CancellationToken ct
        )
        {
            Options = options;
            var list = new List<SubjectInfo>
            {
                new("Matematika", "Marko", "4,00", "100"),
                new("Fizika", "Ivo", "3,00", "300"),
            };
            var pick = options.PickGradePages(list);
            var subjects = list.Select(i => new ScrapedSubject(i, pick.Contains(i.SubjectId) ? Page("4") : null)).ToList();
            var requests = 2 + pick.Count + 1 + (options.FetchSchedule ? 1 : 0);
            return Task.FromResult(new EdnevnikSnapshot(null, subjects, new List<TestInfo>(), null, requests));
        }
    }

    private class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Sync_UsesStoredStateToSkipPages_AndRecordsRequestCount()
    {
        var repo = new Repo
        {
            // Matematika: prosjek se promijenio; Fizika: nepromijenjena, ali ide u rotaciju (2 po syncu)
            State = new()
            {
                ["100"] = new("3,50", Now.AddHours(-1)), // prosjek se promijenio -> skida se
                ["300"] = new("3,00", Now.AddHours(-1)),
            },
            LastSchedule = Now.AddDays(-1),
        };
        var scraper = new Scraper();
        var service = new SyncService(repo, scraper, new FixedTime(), NullLogger<SyncService>.Instance);

        var outcome = await service.SyncAsync(Caller, Child, new List<SyncCookie>(), default);

        Assert.Equal(SyncOutcomeKind.Ok, outcome.Kind);
        Assert.False(scraper.Options!.FetchSchedule);
        var w = repo.Written!;
        Assert.True(w.ChildSubjects.Single(c => c.SubjectId == 1).GradesFetched);
        Assert.Equal(w.ChildSubjects.Count(c => c.GradesFetched) + 3, w.CarnetRequests);
        Assert.Null(w.Timetable);
    }

    [Fact]
    public async Task Sync_Full_FetchesEverythingAndSchedule()
    {
        var repo = new Repo
        {
            State = new() { ["100"] = new("4,00", Now.AddHours(-1)), ["300"] = new("3,00", Now.AddHours(-1)) },
            LastSchedule = Now.AddHours(-1),
        };
        var scraper = new Scraper();
        var service = new SyncService(repo, scraper, new FixedTime(), NullLogger<SyncService>.Instance);

        await service.SyncAsync(Caller, Child, new List<SyncCookie>(), default, full: true);

        Assert.True(scraper.Options!.FetchSchedule);
        Assert.All(repo.Written!.ChildSubjects, c => Assert.True(c.GradesFetched));
        Assert.False(repo.AskedForStoredGrades);
    }

    // --- brojanje zahtjeva ---

    private class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
    }

    [Fact]
    public async Task SessionHandler_CountsResponses()
    {
        var handler = new EdnevnikSessionHandler(TimeProvider.System, TimeSpan.Zero) { InnerHandler = new OkHandler() };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://ocjene.skole.hr/course");
        await client.GetAsync("https://ocjene.skole.hr/exam");

        Assert.Equal(2, handler.Requests);
    }
}

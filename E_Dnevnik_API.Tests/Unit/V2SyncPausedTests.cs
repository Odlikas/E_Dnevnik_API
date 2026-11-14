using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.V2.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

// /v2/sync kad CarnetGuard ne pušta zahtjeve: 503 carnet_paused i zapis u sync_runs
public class V2SyncPausedTests
{
    private static readonly Guid Caller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private class Repo : ISyncRepository
    {
        public (string Status, string? Error)? Finished;

        public bool IsConfigured => true;

        public Task<ChildRecord?> GetChildAsync(Guid childId, CancellationToken ct) =>
            Task.FromResult<ChildRecord?>(new ChildRecord(childId, Guid.NewGuid(), Caller, SelfManaged: true));

        public Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<List<SubjectRow>> GetSubjectsAsync(CancellationToken ct) => Task.FromResult(new List<SubjectRow>());

        public Task<short> EdnevnikSubjectIdAsync(string ednevnikName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Guid> StartRunAsync(Guid childId, Guid familyId, Guid actorId, string? version, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());

        public Task<int> WriteAsync(SyncWrite write, CancellationToken ct) => throw new NotSupportedException();

        public Task FinishRunAsync(Guid runId, string status, int changed, string? errorCode, CancellationToken ct)
        {
            Finished = (status, errorCode);
            return Task.CompletedTask;
        }
    }

    private class PausedScraper : IEdnevnikSyncScraper
    {
        public Task<EdnevnikSnapshot> ScrapeAsync(IReadOnlyList<SyncCookie> cookies, string userKey, CancellationToken ct) =>
            throw new CarnetPausedException(CarnetPauseReason.CircuitOpen, TimeSpan.FromMinutes(4));
    }

    [Fact]
    public async Task Sync_WhenCarnetPaused_ReturnsPausedAndRecordsRun()
    {
        var repo = new Repo();
        var service = new SyncService(repo, new PausedScraper(), TimeProvider.System, NullLogger<SyncService>.Instance);

        var outcome = await service.SyncAsync(Caller, Child, new List<SyncCookie>(), default);

        Assert.Equal(SyncOutcomeKind.CarnetPaused, outcome.Kind);
        Assert.Equal(TimeSpan.FromMinutes(4), outcome.RetryAfter);
        Assert.Equal(("failed", SyncErrorCodes.CarnetPaused), repo.Finished);
    }

    [Fact]
    public void RetryAfterSeconds_DefaultsToAMinute()
    {
        Assert.Equal(60, E_Dnevnik_API.Controllers.V2SyncController.RetryAfterSeconds(null));
        Assert.Equal(240, E_Dnevnik_API.Controllers.V2SyncController.RetryAfterSeconds(TimeSpan.FromMinutes(4)));
    }
}

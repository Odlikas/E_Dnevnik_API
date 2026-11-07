using System.Reflection;

namespace E_Dnevnik_API.V2.Sync
{
    public enum SyncOutcomeKind
    {
        Ok,
        Forbidden,
        SessionInvalid,
        Unavailable,
    }

    public record SyncOutcome(SyncOutcomeKind Kind, SyncResponse? Response = null);

    public interface ISyncService
    {
        Task<SyncOutcome> SyncAsync(Guid callerId, Guid childId, IReadOnlyList<SyncCookie> cookies, CancellationToken ct);
    }

    // /v2/sync: provjera pristupa -> scrape -> upis u jednoj transakciji -> sažetak
    public class SyncService : ISyncService
    {
        // najviše 2 synca odjednom na cijelom servisu; ostali čekaju
        private static readonly SemaphoreSlim Gate = new(2, 2);

        private readonly ISyncRepository _repo;
        private readonly IEdnevnikSyncScraper _scraper;
        private readonly TimeProvider _time;
        private readonly ILogger<SyncService> _logger;

        public SyncService(
            ISyncRepository repo,
            IEdnevnikSyncScraper scraper,
            TimeProvider time,
            ILogger<SyncService> logger
        )
        {
            _repo = repo;
            _scraper = scraper;
            _time = time;
            _logger = logger;
        }

        public static string? ServiceVersion { get; } =
            Environment.GetEnvironmentVariable("SERVICE_VERSION")
            ?? Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

        // roditelj iz iste obitelji ili samostalni učenik sam za sebe
        public static bool CanSync(ChildRecord child, Guid callerId, bool callerIsParentInFamily) =>
            callerIsParentInFamily || (child.SelfManaged && child.AuthUserId == callerId);

        public async Task<SyncOutcome> SyncAsync(
            Guid callerId,
            Guid childId,
            IReadOnlyList<SyncCookie> cookies,
            CancellationToken ct
        )
        {
            var child = await _repo.GetChildAsync(childId, ct);
            if (child is null)
                return new SyncOutcome(SyncOutcomeKind.Forbidden);
            var isParent = await _repo.IsParentInFamilyAsync(callerId, child.FamilyId, ct);
            if (!CanSync(child, callerId, isParent))
                return new SyncOutcome(SyncOutcomeKind.Forbidden);

            await Gate.WaitAsync(ct);
            try
            {
                return await RunAsync(callerId, child, cookies, ct);
            }
            finally
            {
                Gate.Release();
            }
        }

        private async Task<SyncOutcome> RunAsync(
            Guid callerId,
            ChildRecord child,
            IReadOnlyList<SyncCookie> cookies,
            CancellationToken ct
        )
        {
            var runId = await _repo.StartRunAsync(child.Id, child.FamilyId, callerId, ServiceVersion, ct);
            var today = SchoolYear.ZagrebToday(_time);
            var year = SchoolYear.Containing(today);

            EdnevnikSnapshot snapshot;
            try
            {
                snapshot = await _scraper.ScrapeAsync(cookies, child.Id.ToString("D"), ct);
            }
            catch (EdnevnikSessionInvalidException)
            {
                await _repo.FinishRunAsync(runId, "login_error", 0, SyncErrorCodes.SessionInvalid, CancellationToken.None);
                return new SyncOutcome(SyncOutcomeKind.SessionInvalid);
            }
            catch (EdnevnikUnavailableException)
            {
                await _repo.FinishRunAsync(runId, "failed", 0, SyncErrorCodes.Unavailable, CancellationToken.None);
                return new SyncOutcome(SyncOutcomeKind.Unavailable);
            }

            try
            {
                var plan = Plan(child.Id, snapshot, new SubjectMapper(await _repo.GetSubjectsAsync(ct)), year, today);
                var newGrades = await _repo.WriteAsync(
                    new SyncWrite(runId, child.Id, child.FamilyId, year, plan.ChildSubjects, plan.Grades, plan.Tests),
                    ct
                );
                plan.Response.NewGrades = newGrades;
                return new SyncOutcome(SyncOutcomeKind.Ok, plan.Response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[v2/sync] write failed for child {ChildId}", child.Id);
                await _repo.FinishRunAsync(runId, "failed", 0, SyncErrorCodes.Internal, CancellationToken.None);
                throw;
            }
        }

        public record SyncPlan(
            List<ChildSubjectRow> ChildSubjects,
            List<GradeRow> Grades,
            List<CalendarTestRow> Tests,
            SyncResponse Response
        );

        // čisti dio: od skinutih stranica do redaka za bazu i sažetka (NewGrades puni pozivatelj)
        public static SyncPlan Plan(
            Guid childId,
            EdnevnikSnapshot snapshot,
            SubjectMapper mapper,
            SchoolYear year,
            DateOnly today
        )
        {
            var childSubjects = new List<ChildSubjectRow>();
            var grades = new List<GradeRow>();
            var unmapped = new List<string>();
            var averages = new List<(string, IReadOnlyCollection<short>)>();
            var seenSubjects = new HashSet<short>();

            foreach (var s in snapshot.Subjects)
            {
                var subject = mapper.Map(s.Info.SubjectName);
                if (subject is null)
                {
                    if (!unmapped.Contains(s.Info.SubjectName))
                        unmapped.Add(s.Info.SubjectName);
                    continue;
                }
                // dva e-Dnevnik predmeta na isti subject: vrijedi prvi
                if (!seenSubjects.Add(subject.Id))
                    continue;

                childSubjects.Add(
                    new ChildSubjectRow(subject.Id, s.Info.SubjectId, SyncSummary.OrNull(s.Info.Professor))
                );
                var parsed = GradeRows.Parse(s.Details, year);
                grades.AddRange(GradeRows.WithHashes(childId, subject.Id, parsed));
                averages.Add((subject.NameHr, parsed.Select(g => g.Value).ToList()));
            }

            var tests = new List<ParsedTest>();
            foreach (var t in snapshot.Tests)
            {
                var date = year.ParseDate(t.TestDate);
                if (date is null)
                    continue;
                var subjectName = t.TestName?.Trim() ?? "";
                var description = t.TestDescription?.Trim();
                var title = string.IsNullOrEmpty(description) ? subjectName : description;
                if (string.IsNullOrEmpty(title))
                    continue;
                tests.Add(new ParsedTest(subjectName, mapper.Map(subjectName)?.Id, title, date.Value));
            }

            var (weakest, weakestAvg) = SyncSummary.Weakest(averages);
            var next = SyncSummary.NextTest(tests, today);
            var profile = snapshot.Profile;

            var response = new SyncResponse
            {
                School = SyncSummary.OrNull(profile?.StudentSchool),
                ClassName = SyncSummary.OrNull(profile?.StudentGrade),
                HomeroomTeacher = SyncSummary.OrNull(profile?.ClassMaster),
                SubjectCount = snapshot.Subjects.Count,
                WeakestSubject = weakest,
                WeakestAverage = weakestAvg,
                NextTestSubject = next is null ? null : SyncSummary.OrNull(next.SubjectName),
                NextTestOn = next?.Date.ToString("yyyy-MM-dd"),
                UnmappedSubjects = unmapped,
            };

            return new SyncPlan(
                childSubjects,
                grades,
                tests
                    .Where(t => t.Date >= year.FirstDay && t.Date <= year.LastDay)
                    .Select(t => new CalendarTestRow(t.SubjectId, t.Title, t.Date))
                    .ToList(),
                response
            );
        }
    }
}

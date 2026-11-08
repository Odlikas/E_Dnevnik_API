using System.Net;
using E_Dnevnik_API.Models.ScrapeStudentProfile;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.Models.ScrapeTests;
using E_Dnevnik_API.ScrapingServices;

namespace E_Dnevnik_API.V2.Sync
{
    // GradeHtml: sirova stranica /grade/{id}, parsira je GradePageParser
    public record ScrapedSubject(SubjectInfo Info, string? GradeHtml);

    // ScheduleHtml: sirova stranica /schedule (SchedulePageParser), null kad je nismo uspjeli skinuti
    public record EdnevnikSnapshot(
        StudentProfileInfo? Profile,
        List<ScrapedSubject> Subjects,
        List<TestInfo> Tests,
        string? ScheduleHtml = null
    );

    public interface IEdnevnikSyncScraper
    {
        // baca EdnevnikSessionInvalidException ili EdnevnikUnavailableException
        Task<EdnevnikSnapshot> ScrapeAsync(IReadOnlyList<SyncCookie> cookies, string userKey, CancellationToken ct);
    }

    // vrti postojeće scrapere s kolačićima s mobitela; stranice ocjena skida sam (sirovi HTML)
    public class EdnevnikSyncScraper : IEdnevnikSyncScraper
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly ScraperService _subjects;
        private readonly StudentProfileScraperService _profile;
        private readonly TestScraperService _tests;
        private readonly TimeProvider _time;
        private readonly ILogger<EdnevnikSyncScraper> _logger;

        public EdnevnikSyncScraper(
            ScraperService subjects,
            StudentProfileScraperService profile,
            TestScraperService tests,
            TimeProvider time,
            ILogger<EdnevnikSyncScraper> logger
        )
        {
            _subjects = subjects;
            _profile = profile;
            _tests = tests;
            _time = time;
            _logger = logger;
        }

        public static CookieContainer BuildCookies(IEnumerable<SyncCookie> cookies)
        {
            var container = new CookieContainer();
            foreach (var c in cookies)
                container.Add(new Cookie(c.Name!, c.Value ?? "", "/", "ocjene.skole.hr"));
            return container;
        }

        // sirovi HTML /grade/{id}; isti client, pa razmak i provjera mrtve sesije vrijede i tu
        public static async Task<string> FetchGradePageAsync(HttpClient client, string subjectId, CancellationToken ct)
        {
            using var response = await client.GetAsync(
                $"https://ocjene.skole.hr/grade/{Uri.EscapeDataString(subjectId)}",
                ct
            );
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"grade page returned {(int)response.StatusCode}",
                    null,
                    response.StatusCode
                );
            return await response.Content.ReadAsStringAsync(ct);
        }

        // sirovi HTML /schedule; raspored nije bitan za ocjene, pa greška ovdje ne ruši sync
        // (null -> postojeći raspored u bazi ostaje); mrtva sesija se i dalje baca
        private async Task<string?> FetchSchedulePageAsync(HttpClient client, string userKey, CancellationToken ct)
        {
            try
            {
                using var response = await client.GetAsync("https://ocjene.skole.hr/schedule", ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "[v2/sync] schedule page returned {Status} for child {ChildId}",
                        (int)response.StatusCode,
                        userKey
                    );
                    return null;
                }
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
                when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(
                    "[v2/sync] schedule page failed for child {ChildId}: {Type}",
                    userKey,
                    ex.GetType().Name
                );
                return null;
            }
        }

        public async Task<EdnevnikSnapshot> ScrapeAsync(
            IReadOnlyList<SyncCookie> cookies,
            string userKey,
            CancellationToken ct
        )
        {
            using var client = CarnetHttp.CreateClient(
                BuildCookies(cookies),
                CarnetRequestSource.UserScrape,
                userKey,
                RequestTimeout,
                outerHandler: new EdnevnikSessionHandler(_time)
            );

            try
            {
                // prvo profil: najlakša stranica, a odmah otkriva mrtav kolačić
                var profile = (await _profile.ScrapeStudentProfile(client)).StudentProfile;
                var list = (await _subjects.ScrapeSubjects(client)).Subjects ?? new();

                var subjects = new List<ScrapedSubject>();
                foreach (var info in list)
                {
                    ct.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(info.SubjectId))
                        continue;
                    // sat razrednika nema ocjena, ne trošimo zahtjev
                    if (SubjectMapper.IsHomeroom(info.SubjectName))
                    {
                        subjects.Add(new ScrapedSubject(info, null));
                        continue;
                    }
                    subjects.Add(new ScrapedSubject(info, await FetchGradePageAsync(client, info.SubjectId, ct)));
                }

                List<TestInfo> tests;
                try
                {
                    tests = (await _tests.ScrapeTests(client)).Values.SelectMany(t => t).ToList();
                }
                catch (NullReferenceException)
                {
                    // TestScraperService pada kad na /exam nema nijedne exam-table tablice (nema ispita)
                    tests = new List<TestInfo>();
                }

                var schedule = await FetchSchedulePageAsync(client, userKey, ct);

                return new EdnevnikSnapshot(profile, subjects, tests, schedule);
            }
            catch (EdnevnikSessionInvalidException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 5xx, timeout, mreža ili parsiranje; poruka bez kolačića
                _logger.LogWarning(
                    "[v2/sync] e-Dnevnik scrape failed for child {ChildId}: {Type} {Message}",
                    userKey,
                    ex.GetType().Name,
                    ex.Message
                );
                throw new EdnevnikUnavailableException("e-Dnevnik scrape failed.", ex);
            }
        }
    }
}

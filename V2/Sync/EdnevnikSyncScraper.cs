using System.Net;
using E_Dnevnik_API.Models.ScrapeStudentProfile;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.Models.ScrapeTests;
using E_Dnevnik_API.Models.SpecificSubject;
using E_Dnevnik_API.ScrapingServices;

namespace E_Dnevnik_API.V2.Sync
{
    public record ScrapedSubject(SubjectInfo Info, SubjectDetails Details);

    public record EdnevnikSnapshot(
        StudentProfileInfo? Profile,
        List<ScrapedSubject> Subjects,
        List<TestInfo> Tests
    );

    public interface IEdnevnikSyncScraper
    {
        // baca EdnevnikSessionInvalidException ili EdnevnikUnavailableException
        Task<EdnevnikSnapshot> ScrapeAsync(
            IReadOnlyList<SyncCookie> cookies,
            string userKey,
            CancellationToken ct
        );
    }

    // vrti postojeće scrapere s kolačićima s mobitela
    public class EdnevnikSyncScraper : IEdnevnikSyncScraper
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly ScraperService _subjects;
        private readonly SpecificSubjectScraperService _subject;
        private readonly StudentProfileScraperService _profile;
        private readonly TestScraperService _tests;
        private readonly TimeProvider _time;
        private readonly ILogger<EdnevnikSyncScraper> _logger;

        public EdnevnikSyncScraper(
            ScraperService subjects,
            SpecificSubjectScraperService subject,
            StudentProfileScraperService profile,
            TestScraperService tests,
            TimeProvider time,
            ILogger<EdnevnikSyncScraper> logger
        )
        {
            _subjects = subjects;
            _subject = subject;
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
                    var details = await _subject.ScrapeSubjects(client, info.SubjectId);
                    subjects.Add(new ScrapedSubject(info, details));
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

                return new EdnevnikSnapshot(profile, subjects, tests);
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

using System.Net;
using E_Dnevnik_API.Database.Models;
using E_Dnevnik_API.Models.Absences_izostanci;
using E_Dnevnik_API.Models.DifferentGradeLinks;
using E_Dnevnik_API.Models.NewGrades;
using E_Dnevnik_API.Models.NewTests;
using E_Dnevnik_API.Models.ScheduleTable;
using E_Dnevnik_API.Models.ScrapeStudentProfile;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.Models.ScrapeTests;
using E_Dnevnik_API.Models.SpecificSubject;
using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace E_Dnevnik_API.Controllers
{
    [Route("api/[controller]")]
    public class ScraperController : ApiBaseController
    {
        private readonly ScraperService _subjectScraperService;
        private readonly SpecificSubjectScraperService _specificSubjectScraperService;
        private readonly TestScraperService _testScraperService;
        private readonly StudentProfileScraperService _studentProfileScraperService;
        private readonly DifferentGradeLinkScraperService _differentGradeLinkScraperService;
        private readonly AbsenceScraperService _absenceScraperService;
        private readonly ScheduleTableScraperService _scheduleTableScraperService;
        private readonly NewGradesScraperService _newGradesScraperService;
        private readonly NewTestsScraperService _newTestsScraperService;
        private readonly IMemoryCache _memoryCache;
        private readonly CacheService _cache;
        private readonly GradeChangeDetectionService _gradeDetector;

        public ScraperController(
            ScraperService subjectScraperService,
            SpecificSubjectScraperService specificSubjectScraperService,
            TestScraperService testScraperService,
            StudentProfileScraperService studentProfileScraperService,
            DifferentGradeLinkScraperService differentGradeLinkScraperService,
            AbsenceScraperService absenceScraperService,
            ScheduleTableScraperService scheduleTableScraperService,
            NewGradesScraperService newGradesScraperService,
            NewTestsScraperService newTestsScraperService,
            SessionStore sessionStore,
            IMemoryCache memoryCache,
            CacheService cache,
            GradeChangeDetectionService gradeDetector
        )
            : base(sessionStore)
        {
            _subjectScraperService = subjectScraperService;
            _specificSubjectScraperService = specificSubjectScraperService;
            _testScraperService = testScraperService;
            _studentProfileScraperService = studentProfileScraperService;
            _differentGradeLinkScraperService = differentGradeLinkScraperService;
            _absenceScraperService = absenceScraperService;
            _scheduleTableScraperService = scheduleTableScraperService;
            _newGradesScraperService = newGradesScraperService;
            _newTestsScraperService = newTestsScraperService;
            _memoryCache = memoryCache;
            _cache = cache;
            _gradeDetector = gradeDetector;
        }

        // --- Helpers ---

        private static HttpClient CreateClient(CookieContainer cookies, string? email) =>
            CarnetHttp.CreateClient(cookies, CarnetRequestSource.UserScrape, email);

        // Validates token and resolves both cookies and email. Returns null values on failure.
        private (string? token, CookieContainer? cookies, string? email) ResolveSession()
        {
            var token = GetBearerToken();
            if (token is null)
                return (null, null, null);
            var cookies = SessionStore.GetCookies(token);
            if (cookies is null)
                return (token, null, null);
            var email = SessionStore.GetEmailByToken(token);
            return (token, cookies, email);
        }

        // --- Cached endpoints ---

        [HttpGet("ScrapeSubjectsAndProfessors")]
        public async Task<IActionResult> ScrapeSubjects([FromQuery] bool forceRefresh = false)
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<SubjectScrapeResult>(
                    email,
                    c => c.GradesData,
                    c => c.GradesCachedAt,
                    (c, json, time) =>
                    {
                        c.GradesData = json;
                        c.GradesCachedAt = time;
                    },
                    CacheService.GradesTTL,
                    () => _subjectScraperService.ScrapeSubjects(client),
                    forceRefresh
                );

                // Fire-and-forget grade drop detection — does not block the response
                _ = Task.Run(() => _gradeDetector.CheckForDrops(email, data));

                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeSpecificSubjectGrades")]
        public async Task<IActionResult> ScrapeSpecificSubjectGrades(
            [FromQuery] string subjectId,
            [FromQuery] bool forceRefresh = false
        )
        {
            if (string.IsNullOrEmpty(subjectId))
                return BadRequest("Subject ID mora biti unesen.");
            if (!subjectId.All(char.IsDigit))
                return BadRequest("Subject ID mora biti broj.");

            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                // Cache is a per-student JSON dict keyed by subjectId
                var cache = await _cache.GetRawCache(email);
                if (cache == null)
                {
                    cache = new StudentCache { Email = email };
                }

                var dict =
                    cache.SpecificSubjectGradesJson != null
                        ? JsonConvert.DeserializeObject<Dictionary<string, SubjectDetails>>(
                            cache.SpecificSubjectGradesJson
                        ) ?? new()
                        : new Dictionary<string, SubjectDetails>();

                var isFresh =
                    cache.SpecificSubjectGradesCachedAt != null
                    && cache.SpecificSubjectGradesCachedAt.Value
                        > DateTime.UtcNow - CacheService.GradesTTL;

                // Apply force-refresh cooldown
                var cooldownActive =
                    cache.LastForceRefreshAt != null
                    && cache.LastForceRefreshAt > DateTime.UtcNow - TimeSpan.FromMinutes(15);
                if (forceRefresh && cooldownActive)
                    forceRefresh = false;

                if (!forceRefresh && isFresh && dict.ContainsKey(subjectId))
                {
                    return Ok(
                        new
                        {
                            data = dict[subjectId],
                            cachedAt = cache.SpecificSubjectGradesCachedAt,
                            isFromCache = true,
                        }
                    );
                }

                var fresh = await _specificSubjectScraperService.ScrapeSubjects(client, subjectId);
                var now = DateTime.UtcNow;
                return Ok(
                    new
                    {
                        data = fresh,
                        cachedAt = now,
                        isFromCache = false,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeStudentProfile")]
        public async Task<IActionResult> ScrapeStudentProfile([FromQuery] bool forceRefresh = false)
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<StudentProfileResult>(
                    email,
                    c => c.ProfileData,
                    c => c.ProfileCachedAt,
                    (c, json, time) =>
                    {
                        c.ProfileData = json;
                        c.ProfileCachedAt = time;
                    },
                    CacheService.ProfileTTL,
                    () => _studentProfileScraperService.ScrapeStudentProfile(client),
                    forceRefresh
                );
                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeTests")]
        public async Task<IActionResult> ScrapeTests([FromQuery] bool forceRefresh = false)
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<
                    Dictionary<string, List<TestInfo>>
                >(
                    email,
                    c => c.TestsData,
                    c => c.TestsCachedAt,
                    (c, json, time) =>
                    {
                        c.TestsData = json;
                        c.TestsCachedAt = time;
                    },
                    CacheService.TestsTTL,
                    () => _testScraperService.ScrapeTests(client),
                    forceRefresh
                );
                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeScheduleTable")]
        public async Task<IActionResult> ScrapeScheduleTable([FromQuery] bool forceRefresh = false)
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<ScheduleResult>(
                    email,
                    c => c.ScheduleData,
                    c => c.ScheduleCachedAt,
                    (c, json, time) =>
                    {
                        c.ScheduleData = json;
                        c.ScheduleCachedAt = time;
                    },
                    CacheService.ScheduleTTL,
                    () => _scheduleTableScraperService.ScrapeScheduleTable(client),
                    forceRefresh
                );
                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeAbsences")]
        public async Task<IActionResult> ScrapeAbsences([FromQuery] bool forceRefresh = false)
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<AbsencesResult>(
                    email,
                    c => c.AbsencesData,
                    c => c.AbsencesCachedAt,
                    (c, json, time) =>
                    {
                        c.AbsencesData = json;
                        c.AbsencesCachedAt = time;
                    },
                    CacheService.AbsencesTTL,
                    () => _absenceScraperService.ScrapeAbsences(client),
                    forceRefresh
                );
                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeDifferentGrades")]
        public async Task<IActionResult> ScrapeDifferentGradeLink(
            [FromQuery] bool forceRefresh = false
        )
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null || email is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var (data, cachedAt, fromCache) = await _cache.GetOrFetch<
                    List<GradeSubjectDetails>
                >(
                    email,
                    c => c.GradesDifferentData,
                    c => c.GradesDifferentCachedAt,
                    (c, json, time) =>
                    {
                        c.GradesDifferentData = json;
                        c.GradesDifferentCachedAt = time;
                    },
                    CacheService.GradesDifferentTTL,
                    () => _differentGradeLinkScraperService.ScrapeDifferentGradeLink(client),
                    forceRefresh
                );
                return Ok(
                    new
                    {
                        data,
                        cachedAt,
                        isFromCache = fromCache,
                    }
                );
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        // --- IMemoryCache endpoints (unchanged from original) ---

        [HttpGet("ScrapeNewGrades")]
        public async Task<ActionResult<NewGradesResult>> ScrapeNewGrades()
        {
            var token = GetBearerToken();
            if (token is null)
                return Unauthorized("Authorization header s Bearer tokenom je obavezan.");
            var cookies = SessionStore.GetCookies(token);
            if (cookies is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            if (
                _memoryCache.TryGetValue<NewGradesResult>($"newgrades:{token}", out var cached)
                && cached is not null
            )
            {
                CarnetRequestStats.Shared.RecordCacheHit();
                return Ok(cached);
            }

            using var client = CreateClient(cookies, SessionStore.GetEmailByToken(token));
            try
            {
                var result = await _newGradesScraperService.ScrapeNewGrades(client);
                _memoryCache.Set($"newgrades:{token}", result, TimeSpan.FromMinutes(10));
                return Ok(result);
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("ScrapeNewTests")]
        public async Task<ActionResult<NewTestsResult>> ScrapeNewTests()
        {
            var token = GetBearerToken();
            if (token is null)
                return Unauthorized("Authorization header s Bearer tokenom je obavezan.");
            var cookies = SessionStore.GetCookies(token);
            if (cookies is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            if (
                _memoryCache.TryGetValue<NewTestsResult>($"newtests:{token}", out var cached)
                && cached is not null
            )
            {
                CarnetRequestStats.Shared.RecordCacheHit();
                return Ok(cached);
            }

            using var client = CreateClient(cookies, SessionStore.GetEmailByToken(token));
            try
            {
                var result = await _newTestsScraperService.ScrapeNewTests(client);
                _memoryCache.Set($"newtests:{token}", result, TimeSpan.FromMinutes(10));
                return Ok(result);
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        [HttpGet("CalculateMissedClassPercentages")]
        public async Task<
            ActionResult<Dictionary<string, string>>
        > CalculateMissedClassPercentages()
        {
            var (_, cookies, email) = ResolveSession();
            if (cookies is null)
                return Unauthorized(
                    "Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
                );

            using var client = CreateClient(cookies, email);
            try
            {
                var scheduleData = await _scheduleTableScraperService.ScrapeScheduleTable(client);
                var yearlyHours = _scheduleTableScraperService.CalculateYearlySubjectHours(
                    scheduleData
                );
                var absencesData = await _absenceScraperService.ScrapeAbsences(client);
                var daysMissed = _absenceScraperService.CalculateDaysMissed(absencesData);
                return Ok(CalculateMissedPercentages(yearlyHours, daysMissed));
            }
            catch (ScraperException ex)
            {
                return StatusCode(ex.StatusCode, ex.Message);
            }
        }

        private Dictionary<string, string> CalculateMissedPercentages(
            Dictionary<string, int> yearlyHours,
            Dictionary<string, int> daysMissed
        )
        {
            var percentages = new Dictionary<string, string>();
            foreach (var subject in yearlyHours)
            {
                var totalHours = subject.Value;
                if (totalHours == 0)
                {
                    percentages[subject.Key] = "0.00%";
                    continue;
                }
                var missedDays = daysMissed.ContainsKey(subject.Key) ? daysMissed[subject.Key] : 0;
                percentages[subject.Key] = $"{(double)missedDays / totalHours * 100:N2}%";
            }
            return percentages;
        }
    }
}

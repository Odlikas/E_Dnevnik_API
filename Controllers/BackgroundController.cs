using E_Dnevnik_API.Database;
using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace E_Dnevnik_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class BackgroundController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly SessionStore _sessionStore;
        private readonly ScraperService _gradesScraperService;
        private readonly GradeChangeDetectionService _detector;
        private readonly FcmService _fcm;
        private readonly IConfiguration _config;
        private readonly ILogger<BackgroundController> _logger;

        public BackgroundController(
            AppDbContext db,
            SessionStore sessionStore,
            ScraperService gradesScraperService,
            GradeChangeDetectionService detector,
            FcmService fcm,
            IConfiguration config,
            ILogger<BackgroundController> logger
        )
        {
            _db = db;
            _sessionStore = sessionStore;
            _gradesScraperService = gradesScraperService;
            _detector = detector;
            _fcm = fcm;
            _config = config;
            _logger = logger;
        }

        // Called by Firebase Cloud Function. Protected by X-Background-Secret header.
        // Scrapes grades for users with valid sessions, sends reminders for expired ones.
        [HttpPost("CheckNewGrades")]
        public async Task<IActionResult> CheckNewGrades(
            [FromHeader(Name = "X-Background-Secret")] string? secret
        )
        {
            if (secret != _config["BackgroundJobSecret"])
                return Unauthorized();

            // Only process users who have monitored subjects and were active in last 30 days
            var emails = await _db
                .MonitoredSubjects.Join(
                    _db.StudentCache,
                    m => m.Email,
                    c => c.Email,
                    (m, c) =>
                        new
                        {
                            c.Email,
                            c.ActiveToken,
                            c.TokenStoredAt,
                            c.LastActiveAt,
                        }
                )
                .Where(x => x.LastActiveAt > DateTime.UtcNow.AddDays(-30))
                .Select(x => new
                {
                    x.Email,
                    x.ActiveToken,
                    x.TokenStoredAt,
                    x.LastActiveAt,
                })
                .Distinct()
                .ToListAsync();

            int scraped = 0,
                remindersSent = 0,
                failed = 0;
            var backedOff = false;

            foreach (var user in emails)
            {
                try
                {
                    // Token stored within last 23h = session likely still valid in SessionStore
                    var tokenAge =
                        user.TokenStoredAt != null
                            ? DateTime.UtcNow - user.TokenStoredAt.Value
                            : TimeSpan.MaxValue;
                    var hasValidSession =
                        user.ActiveToken != null && tokenAge < TimeSpan.FromHours(23);

                    if (hasValidSession)
                    {
                        var cookies = _sessionStore.GetCookies(user.ActiveToken!);
                        if (cookies != null)
                        {
                            using var client = CarnetHttp.CreateClient(
                                cookies,
                                CarnetRequestSource.Background,
                                user.Email,
                                TimeSpan.FromSeconds(60)
                            );

                            Models.ScrapeSubjects.SubjectScrapeResult grades;
                            try
                            {
                                grades = await _gradesScraperService.ScrapeSubjects(client);
                            }
                            catch (Exception ex) when (CarnetBackoff.ShouldBackOff(ex))
                            {
                                // CARNET je preopterećen (429/5xx/timeout) - prekidamo cijeli run, idući je za par sati
                                _logger.LogWarning(
                                    ex,
                                    "[Background] CARNET backoff: prekidam run nakon {Scraped} od {Total} korisnika",
                                    scraped,
                                    emails.Count
                                );
                                backedOff = true;
                                break;
                            }

                            // Update Postgres cache
                            var cache = await _db.StudentCache.FindAsync(user.Email);
                            if (cache != null)
                            {
                                cache.GradesData = JsonConvert.SerializeObject(grades);
                                cache.GradesCachedAt = DateTime.UtcNow;
                                cache.LastActiveAt = DateTime.UtcNow;
                            }
                            await _db.SaveChangesAsync();

                            await _detector.CheckForDrops(user.Email, grades);
                            scraped++;
                        }
                        else
                        {
                            // Session expired (dyno restart) — clear stale token
                            var cache = await _db.StudentCache.FindAsync(user.Email);
                            if (cache != null)
                            {
                                cache.ActiveToken = null;
                                await _db.SaveChangesAsync();
                            }
                            await SendReminder(user.Email);
                            remindersSent++;
                        }
                    }
                    else
                    {
                        // No valid session — send reminder if not active today
                        if (user.LastActiveAt.Date != DateTime.UtcNow.Date)
                        {
                            await SendReminder(user.Email);
                            remindersSent++;
                        }
                    }

                    await Task.Delay(500); // 500ms between users — good CARNET citizen
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Background] Failed for {Email}", user.Email);
                    failed++;
                }
            }

            return Ok(
                new
                {
                    scraped,
                    remindersSent,
                    failed,
                    backedOff,
                    total = emails.Count,
                }
            );
        }

        private async Task SendReminder(string email)
        {
            await _fcm.SendNotification(
                email: email,
                title: "Provjeri svoje ocjene",
                body: "Otvori Odlikas i provjeri ima li novih ocjena danas."
            );
        }
    }
}

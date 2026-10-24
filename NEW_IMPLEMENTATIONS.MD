# Backend Caching & Database Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add PostgreSQL to the ASP.NET Core 8 backend, implement per-student caching for all CARNET scraper endpoints, and build the background job infrastructure that powers smart notifications, streaks, and leaderboard.

**Architecture:** ASP.NET Core 8 + Entity Framework Core + Npgsql (PostgreSQL). Cache lives in Postgres. All scraper controllers route through a CacheService before touching CARNET. Firebase Cloud Scheduler triggers background grade checks 2x/day.

**Tech Stack:** ASP.NET Core 8, Entity Framework Core, Npgsql, PostgreSQL (Heroku), Firebase Cloud Functions (scheduler), OpenAI API (gpt-4o-mini)

**Branch:** `backend/caching-and-database`

---

## Project Context — Read This First

This backend serves **Odlikas**, a Flutter mobile app for Croatian secondary school students. The app scrapes academic data (grades, schedule, tests, absences) from the Croatian national school system (**CARNET / e-Dnevnik**, hosted at `ocjene.skole.hr`). CARNET has not formally approved or rejected scraping. Behave as a responsible API citizen — no hammering their servers.

### The app's purpose

Odlikas gamifies studying. The core engagement loop is:

```
Bad grade detected on a monitored subject
        ↓
Backend sends FCM push notification
        ↓
Student opens app → sees AI-generated practice tasks for that subject
        ↓
Student completes tasks → counts as a study session → streak increments
        ↓
Streak improves their leaderboard score → motivation to keep going
```

The leaderboard ranks students by **grade improvement over time** (not raw grades), combined with streak consistency. A student going from 2.5 to 3.5 average outranks a student who stays at 5.0. This rewards growth, not existing excellence.

### Current auth flow (critical to understand before touching anything)

```
1. Flutter app: POST /api/Login { email, password }
2. Backend: authenticates against CARNET, creates a server-side session
3. Backend: generates its own token (base64, 24h sliding window), holds session in memory
4. Backend: returns { token } to Flutter app
5. Flutter: stores token in flutter_secure_storage (NOT Hive)
6. Every API call: Flutter attaches "Authorization: Bearer <token>" header
7. Backend: validates token → looks up in-memory CARNET session → scrapes → returns data
8. On 401: Flutter's AuthInterceptor silently re-logins using stored credentials,
           saves new token, retries the failed request — transparent to the user
9. Heroku eco dynos restart daily → in-memory sessions wiped → all users get 401
   on next request → AuthInterceptor handles silently
```

**What this means for the background job:** The background job runs 2x per day at 15:00 and 22:00 Zagreb time. It scrapes CARNET directly using the student's stored in-memory session. After a Heroku restart (eco dynos restart daily), sessions are wiped — for students whose session is gone, the job skips the CARNET scrape and sends a gentle "check your grades" reminder notification instead. The background job is the primary mechanism for detecting new grades when the student hasn't opened the app. Note: because eco dynos restart once daily, timing overlap may mean some runs find 0 valid sessions and send reminders instead of scraping — log `scraped` vs `remindersSent` counts per run to monitor this in production.

### CARNET rate limiting strategy

- **Never scrape the same student's data more than once per TTL period** — enforced server-side
- **Force refresh rate limit:** max once per 15 minutes per student, even if they pull-to-refresh repeatedly
- **Background job:** compares cached Postgres data only, no live CARNET scraping
- **500ms delay between users** in any batch processing
- **Back off on CARNET errors:** 429 or 503 → stop processing, log, retry next scheduled run
- **Good citizenship:** use a descriptive User-Agent header, don't disguise requests

### Cache TTLs

| Data | TTL | Reason |
|---|---|---|
| Student profile | 7 days | Name/school/program almost never changes |
| Schedule | 7 days | Changes once per semester at most |
| All grades | 2 hours | Teachers grade a few times per week |
| Specific subject grades | 2 hours | Same |
| Tests | 1 hour | Can change a few times per week |
| Absences | 1 hour | Could change daily |
| Different grades (slow endpoint) | 24 hours | Slow to scrape, changes rarely |

### Planned features this backend must support

1. **Smart notifications** — detect grade drop in a monitored subject → generate practice tasks via OpenAI → send FCM push
2. **Streaks** — track daily Pomodoro session completions, calculate current/longest streak
3. **Leaderboard** — opt-in, nickname-based, score = (grade improvement delta × 0.6) + (streak score × 0.4), weekly score recalculation
4. **Grade baselines** — snapshot each student's average at school year start; delta = current average minus baseline
5. **Subject monitoring** — free tier: 1 subject. Odlikas+ (premium): up to 5 subjects

### What Firestore is still used for (do not move these to Postgres)

- Push notification delivery records (`newNotifications` collection)
- Pomodoro timer state sync between mobile and tablet (real-time, needs Firestore streams)
- Calendar events
- File upload metadata (Odlikas+)

**FCM tokens move to Postgres** (column on `StudentCache`). Flutter calls `POST /api/Device/RegisterToken` right after login and on `FirebaseMessaging.instance.onTokenRefresh`. The backend reads from Postgres when sending push notifications — it never reads from Firestore.

Everything else moves to Postgres.

---

## Why PostgreSQL and not Firestore for caching

Firestore charges per read/write. Caching 50,000 students' scraped data through Firestore would cost hundreds of dollars per month at scale. PostgreSQL on Heroku has a free tier (10,000 rows, 1GB) sufficient for the competition demo, and $9/month paid tiers for production. PostgreSQL also handles the relational data (leaderboard scores, grade baselines, session tracking) far better than Firestore's document model.

---

## Task 1: Add Heroku Postgres

- [ ] Add the Heroku Postgres add-on:

```bash
heroku addons:create heroku-postgresql:essential-0 --app your-app-name
```

- [ ] Verify `DATABASE_URL` is set:

```bash
heroku config --app your-app-name | grep DATABASE_URL
```

Output looks like:
```
DATABASE_URL: postgres://user:password@host:5432/dbname
```

- [ ] Copy this value to your local `.env` or `appsettings.Development.json` for local development.

---

## Task 2: Add NuGet packages

- [ ] Add Entity Framework Core and the Postgres driver:

```bash
dotnet add package Microsoft.EntityFrameworkCore
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add package Microsoft.EntityFrameworkCore.Tools
dotnet add package Microsoft.EntityFrameworkCore.Design
```

---

## Task 3: Create the database models and base controller

Create a `Database/Models/` folder. Add one file per model.

Also create `Controllers/ApiBaseController.cs` — all authenticated controllers inherit from this. Read the existing scraper controllers first to find the current token-to-email resolution (SessionStore lookup), then extract it here so it's not duplicated across every new controller:

```csharp
// Read existing scraper controllers before implementing this.
// The token-to-email resolution is already implemented there — extract it here.
[ApiController]
public abstract class ApiBaseController : ControllerBase
{
    // Inject SessionStore (or whatever the existing mechanism is) via constructor
    // and implement GetEmailFromToken() once here.
    protected abstract string GetEmailFromToken();
}
```

- [ ] Create `Database/Models/StudentCache.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace YourApp.Database.Models;

public class StudentCache
{
    [Key]
    public string Email { get; set; } = string.Empty;

    // Stores the current active Bearer token so the background job
    // can identify which users have active sessions.
    // This is NOT a credential — it is a short-lived session token
    // generated by this backend, valid for 24h.
    public string? ActiveToken { get; set; }
    public DateTime? TokenStoredAt { get; set; }

    // FCM device token — stored here after Flutter calls POST /api/Device/RegisterToken.
    // Backend reads this when sending push notifications. Never read from Firestore.
    public string? FcmToken { get; set; }
    public DateTime? FcmTokenUpdatedAt { get; set; }

    // Set to true after Stripe payment is confirmed via webhook.
    // Backend enforces this when checking monitored subject limits.
    public bool IsOdlikasPlus { get; set; } = false;
    public DateTime? OdlikasPlusSince { get; set; }

    public string? ProfileData { get; set; }
    public DateTime? ProfileCachedAt { get; set; }

    public string? GradesData { get; set; }
    public DateTime? GradesCachedAt { get; set; }

    // Per-subject grades cache — JSON-serialized Dictionary<string, string> keyed by subjectId.
    // Used by ScrapeSpecificSubjectGrades to avoid redundant CARNET scrapes.
    public string? SpecificSubjectGradesJson { get; set; }
    public DateTime? SpecificSubjectGradesCachedAt { get; set; }

    public string? ScheduleData { get; set; }
    public DateTime? ScheduleCachedAt { get; set; }

    public string? TestsData { get; set; }
    public DateTime? TestsCachedAt { get; set; }

    public string? AbsencesData { get; set; }
    public DateTime? AbsencesCachedAt { get; set; }

    public string? GradesDifferentData { get; set; }
    public DateTime? GradesDifferentCachedAt { get; set; }

    public DateTime? LastForceRefreshAt { get; set; }
    public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] Create `Database/Models/GradeSnapshot.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace YourApp.Database.Models;

[PrimaryKey(nameof(Email), nameof(SubjectId))]
public class GradeSnapshot
{
    public string Email { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public decimal LastKnownAverage { get; set; }
    public decimal LastKnownLatestGrade { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] Create `Database/Models/GradeBaseline.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace YourApp.Database.Models;

// Composite key: one baseline per student per school year.
// Using [Key] on Email alone would overwrite last year's baseline on year rollover.
[PrimaryKey(nameof(Email), nameof(SchoolYear))]
public class GradeBaseline
{
    public string Email { get; set; } = string.Empty;
    public string SchoolYear { get; set; } = string.Empty; // e.g. "2025-2026"
    public decimal BaselineAverage { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] Create `Database/Models/PomodoroSession.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace YourApp.Database.Models;

[Index(nameof(Email), nameof(SessionDate), IsUnique = true)]
public class PomodoroSession
{
    [Key]
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public DateOnly SessionDate { get; set; }
    public int SessionsCompleted { get; set; } = 0; // max 8 per day
    public int TotalMinutes { get; set; } = 0;
}
```

- [ ] Create `Database/Models/TaskSet.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace YourApp.Database.Models;

// A set of AI-generated practice tasks sent to a student after a grade drop.
// Completing a task set counts as one study session toward the streak.
public class TaskSet
{
    [Key]
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string TasksJson { get; set; } = "[]"; // JSON array of task strings in Croatian
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool IsCompleted { get; set; } = false;
}
```

- [ ] Create `Database/Models/MonitoredSubject.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace YourApp.Database.Models;

// Which subjects a student has chosen to monitor for grade drop notifications.
// Free tier: max 1 subject. Odlikas+ premium: max 5 subjects.
[PrimaryKey(nameof(Email), nameof(SubjectId))]
public class MonitoredSubject
{
    public string Email { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
}
```

- [ ] Create `Database/Models/LeaderboardEntry.cs`:

```csharp
using System.ComponentModel.DataAnnotations;

namespace YourApp.Database.Models;

// Leaderboard score = (GradeDeltaScore * 0.6) + (StreakScore * 0.4)
// GradeDeltaScore = normalized improvement from GradeBaseline to current average
// StreakScore = normalized current streak (capped at 30 days = max score)
// Both normalized to 0-100 across all opted-in participants in the same scope.
public class LeaderboardEntry
{
    [Key]
    public string Email { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Nickname { get; set; } = string.Empty; // shown publicly, never real name

    // Populated from student's e-Dnevnik profile on opt-in
    public string ClassId { get; set; } = string.Empty;
    public string SchoolId { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string County { get; set; } = string.Empty; // Županija

    public decimal GradeDeltaScore { get; set; } = 0;
    public decimal StreakScore { get; set; } = 0;
    public decimal CombinedScore { get; set; } = 0;
    public int CurrentStreak { get; set; } = 0;

    public DateTime OptedInAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScoreUpdate { get; set; }
}
```

---

## Task 4: Create the DbContext

- [ ] Create `Database/AppDbContext.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using YourApp.Database.Models;

namespace YourApp.Database;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<StudentCache> StudentCache => Set<StudentCache>();
    public DbSet<GradeSnapshot> GradeSnapshots => Set<GradeSnapshot>();
    public DbSet<GradeBaseline> GradeBaselines => Set<GradeBaseline>();
    public DbSet<PomodoroSession> PomodoroSessions => Set<PomodoroSession>();
    public DbSet<TaskSet> TaskSets => Set<TaskSet>();
    public DbSet<MonitoredSubject> MonitoredSubjects => Set<MonitoredSubject>();
    public DbSet<LeaderboardEntry> LeaderboardEntries => Set<LeaderboardEntry>();
}
```

---

## Task 5: Register the database in Program.cs

- [ ] In `Program.cs`, before `builder.Build()`:

```csharp
var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
string connectionString;

if (databaseUrl != null)
{
    // Heroku provides DATABASE_URL as postgres://user:password@host:port/database
    var uri = new Uri(databaseUrl);
    var userInfo = uri.UserInfo.Split(':');
    connectionString = $"Host={uri.Host};Port={uri.Port};" +
                       $"Database={uri.AbsolutePath.TrimStart('/')};" +
                       $"Username={userInfo[0]};Password={userInfo[1]};" +
                       $"SSL Mode=Require;Trust Server Certificate=true";
}
else
{
    connectionString = builder.Configuration.GetConnectionString("DefaultConnection")!;
}

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
```

- [ ] Add to `appsettings.Development.json` for local dev:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=odlikas_dev;Username=postgres;Password=yourpassword"
  }
}
```

---

## Task 6: Run migrations

- [ ] Create and apply the initial migration:

```bash
dotnet ef migrations add InitialSchema
dotnet ef database update
```

- [ ] Verify tables exist:

```bash
heroku pg:psql --app your-app-name
\dt
```

Expected: 7 tables listed (student_cache, grade_snapshots, grade_baselines, pomodoro_sessions, task_sets, monitored_subjects, leaderboard_entries).

---

## Task 7: Update the login endpoint — store token + generate Firebase custom token

Two additions to the login response:
1. Store the token in `StudentCache` so the background job can identify active sessions
2. Generate a Firebase custom token so Flutter can sign into Firebase Auth and get a stable UID

- [ ] Add the Firebase Admin SDK NuGet package:

```bash
dotnet add package FirebaseAdmin
```

- [ ] Initialize Firebase Admin in `Program.cs` (once, before `builder.Build()`):

```csharp
// Heroku has an ephemeral filesystem — files deployed with the app are wiped on dyno restart.
// Never use GoogleCredential.FromFile() on Heroku. Store the service account JSON as an
// env var and parse it at runtime instead.
var serviceAccountJson = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON");
if (serviceAccountJson != null)
{
    FirebaseApp.Create(new AppOptions
    {
        Credential = GoogleCredential.FromJson(serviceAccountJson)
    });
}
```

Download your Firebase service account JSON from Firebase Console → Project Settings → Service Accounts → Generate new private key. Store the **entire JSON content** (not the file path) as a Heroku config var:

```bash
heroku config:set FIREBASE_SERVICE_ACCOUNT_JSON='{"type":"service_account","project_id":"..."}' --app your-app-name
```

- [ ] In the login controller, after validating CARNET and generating your token:

```csharp
// 1. Store token in Postgres
var cache = await _db.StudentCache.FindAsync(email)
            ?? new StudentCache { Email = email };

cache.ActiveToken = generatedToken;
cache.TokenStoredAt = DateTime.UtcNow;
cache.LastActiveAt = DateTime.UtcNow;

if (_db.StudentCache.Local.Contains(cache))
    _db.StudentCache.Update(cache);
else
    _db.StudentCache.Add(cache);

await _db.SaveChangesAsync();

// 2. Generate Firebase custom token — Flutter uses this to get a real Firebase UID
// The UID is deterministic per student: sha256(email) so it's consistent across logins
// and not reversible to the original email.
var uid = Convert.ToHexString(
    System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant())))
    .ToLower()[..28]; // Firebase UIDs max 128 chars, 28 is fine

var firebaseToken = await FirebaseAuth.DefaultInstance.CreateCustomTokenAsync(uid);

return Ok(new { token = generatedToken, firebaseToken, uid });
```

**Why this UID approach:** Using `sha256(email)[..28]` gives a stable, non-PII identifier. The same student always gets the same UID (no Postgres lookup needed), it's not reversible to the email, and it's short enough to use as a Firestore document ID. Firebase Auth custom tokens expire in 1 hour — Flutter signs in once per login and Firebase Auth handles its own refresh internally.

---

## Task 8: Build the CacheService

Every scraper endpoint routes through this service. It checks Postgres first, returns cached data if fresh, and only hits CARNET when data is stale.

- [ ] Create `Services/CacheService.cs`:

```csharp
using System.Text.Json;
using YourApp.Database;
using YourApp.Database.Models;

namespace YourApp.Services;

public class CacheService
{
    private readonly AppDbContext _db;

    public static readonly TimeSpan ProfileTTL = TimeSpan.FromDays(7);
    public static readonly TimeSpan ScheduleTTL = TimeSpan.FromDays(7);
    public static readonly TimeSpan GradesTTL = TimeSpan.FromHours(2);
    public static readonly TimeSpan TestsTTL = TimeSpan.FromHours(1);
    public static readonly TimeSpan AbsencesTTL = TimeSpan.FromHours(1);
    public static readonly TimeSpan GradesDifferentTTL = TimeSpan.FromHours(24);
    private static readonly TimeSpan ForceRefreshCooldown = TimeSpan.FromMinutes(15);

    public CacheService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<(T data, DateTime cachedAt, bool fromCache)> GetOrFetch<T>(
        string email,
        Func<StudentCache, string?> getData,
        Func<StudentCache, DateTime?> getCachedAt,
        Action<StudentCache, string, DateTime> setData,
        TimeSpan ttl,
        Func<Task<T>> fetchFromCarnet,
        bool forceRefresh = false)
    {
        var cache = await _db.StudentCache.FindAsync(email)
                    ?? new StudentCache { Email = email };

        var cachedAt = getCachedAt(cache);
        var isFresh = cachedAt != null && cachedAt.Value > DateTime.UtcNow - ttl;

        // Enforce force refresh cooldown — prevents students from hammering CARNET
        if (forceRefresh &&
            cache.LastForceRefreshAt != null &&
            cache.LastForceRefreshAt > DateTime.UtcNow - ForceRefreshCooldown)
        {
            forceRefresh = false;
        }

        if (!forceRefresh && isFresh && getData(cache) != null)
        {
            cache.LastActiveAt = DateTime.UtcNow;
            _db.StudentCache.Update(cache);
            await _db.SaveChangesAsync();
            return (JsonSerializer.Deserialize<T>(getData(cache)!)!, cachedAt!.Value, true);
        }

        // Cache miss or approved force refresh — scrape CARNET
        var data = await fetchFromCarnet();
        var now = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(data);

        setData(cache, json, now);
        cache.LastActiveAt = now;

        if (forceRefresh)
            cache.LastForceRefreshAt = now;

        if (_db.StudentCache.Local.Contains(cache))
            _db.StudentCache.Update(cache);
        else
            _db.StudentCache.Add(cache);

        await _db.SaveChangesAsync();
        return (data, now, false);
    }

    public async Task<StudentCache?> GetRawCache(string email)
        => await _db.StudentCache.FindAsync(email);
}
```

---

## Task 9: Register CacheService

- [ ] In `Program.cs` before `builder.Build()`:

```csharp
builder.Services.AddScoped<CacheService>();
```

---

## Task 10: Add RegisterToken endpoint and FcmService

Flutter calls `POST /api/Device/RegisterToken` in two places:
1. Right after login succeeds (replaces the old Firestore FCM token write)
2. On `FirebaseMessaging.instance.onTokenRefresh` when Firebase rotates the device token

The backend stores the token in `StudentCache.FcmToken` and reads it from there when sending push notifications. It never reads from Firestore.

- [ ] Create `Services/FcmService.cs`:

```csharp
using FirebaseAdmin.Messaging;
using YourApp.Database;

namespace YourApp.Services;

public class FcmService
{
    private readonly AppDbContext _db;

    public FcmService(AppDbContext db)
    {
        _db = db;
    }

    public async Task SendNotification(string email, string title, string body)
    {
        var cache = await _db.StudentCache.FindAsync(email);
        if (cache?.FcmToken == null)
        {
            // Token not registered yet — skip silently. Flutter will register on next login.
            Console.WriteLine($"[FCM] No token for {email}, skipping notification.");
            return;
        }

        var message = new Message
        {
            Token = cache.FcmToken,
            Notification = new Notification { Title = title, Body = body },
        };

        try
        {
            await FirebaseMessaging.DefaultInstance.SendAsync(message);
        }
        catch (FirebaseMessagingException ex) when (
            ex.MessagingErrorCode == MessagingErrorCode.Unregistered ||
            ex.MessagingErrorCode == MessagingErrorCode.InvalidArgument)
        {
            // Token is stale — clear it so we don't retry until Flutter refreshes it
            cache.FcmToken = null;
            cache.FcmTokenUpdatedAt = null;
            _db.StudentCache.Update(cache);
            await _db.SaveChangesAsync();
        }
    }
}
```

- [ ] Create `Controllers/DeviceController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using YourApp.Database;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DeviceController : ApiBaseController
{
    private readonly AppDbContext _db;

    public DeviceController(AppDbContext db)
    {
        _db = db;
    }

    // Flutter calls this right after login and on FCM token refresh.
    // Requires valid Bearer token — same auth as all scraper endpoints.
    [HttpPost("RegisterToken")]
    public async Task<IActionResult> RegisterToken([FromBody] RegisterTokenDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.FcmToken))
            return BadRequest(new { error = "FcmToken is required." });

        var email = GetEmailFromToken();
        var cache = await _db.StudentCache.FindAsync(email)
                    ?? new StudentCache { Email = email };

        cache.FcmToken = dto.FcmToken;
        cache.FcmTokenUpdatedAt = DateTime.UtcNow;

        if (_db.StudentCache.Local.Contains(cache))
            _db.StudentCache.Update(cache);
        else
            _db.StudentCache.Add(cache);

        await _db.SaveChangesAsync();
        return Ok();
    }
}

public record RegisterTokenDto(string FcmToken);
```

- [ ] Register in `Program.cs`:

```csharp
builder.Services.AddScoped<FcmService>();
```

> **Note:** `ApiBaseController` is the base class you extract `GetEmailFromToken()` into (see note in Task 3). `DeviceController` inherits from it so Bearer token validation is consistent across all authenticated controllers.

---

## Task 11: Update all scraper endpoints to use CacheService

For every scraper endpoint, wrap the CARNET call in `CacheService.GetOrFetch`. Also update the response shape to include `cachedAt` and `isFromCache` so the Flutter app can show "Updated X minutes ago".

- [ ] Inject `CacheService` and `GradeChangeDetectionService` into the scraper controller:

```csharp
private readonly CacheService _cache;
private readonly ICarnetScraper _scraper; // your existing CARNET scraper service
private readonly GradeChangeDetectionService _gradeDetector;

public ScraperController(CacheService cache, ICarnetScraper scraper, GradeChangeDetectionService gradeDetector)
{
    _cache = cache;
    _scraper = scraper;
    _gradeDetector = gradeDetector;
}
```

- [ ] Apply this pattern to every endpoint. Full example for grades:

```csharp
[HttpGet("ScrapeSubjectsAndProfessors")]
public async Task<IActionResult> ScrapeGrades([FromQuery] bool forceRefresh = false)
{
    var email = GetEmailFromToken(); // your existing token-to-email resolution

    var (data, cachedAt, fromCache) = await _cache.GetOrFetch<GradesResponse>(
        email: email,
        getData: c => c.GradesData,
        getCachedAt: c => c.GradesCachedAt,
        setData: (c, json, time) => { c.GradesData = json; c.GradesCachedAt = time; },
        ttl: CacheService.GradesTTL,
        fetchFromCarnet: () => _scraper.ScrapeGrades(email),
        forceRefresh: forceRefresh
    );

    // After fetching grades, run grade change detection for monitored subjects
    // This is how grade drops get detected when a user actively opens the app.
    // Do not await — fire and forget so the user gets their response immediately.
    _ = Task.Run(() => _gradeDetector.CheckForDrops(email, data));

    return Ok(new { data, cachedAt, isFromCache = fromCache });
}
```

Apply to all endpoints with their TTLs:

| Endpoint | getData | getCachedAt | setData fields | TTL constant |
|---|---|---|---|---|
| ScrapeStudentProfile | ProfileData | ProfileCachedAt | ProfileData, ProfileCachedAt | ProfileTTL |
| ScrapeSubjectsAndProfessors | GradesData | GradesCachedAt | GradesData, GradesCachedAt | GradesTTL |
| ScrapeTests | TestsData | TestsCachedAt | TestsData, TestsCachedAt | TestsTTL |
| ScrapeScheduleTable | ScheduleData | ScheduleCachedAt | ScheduleData, ScheduleCachedAt | ScheduleTTL |
| ScrapeAbsences | AbsencesData | AbsencesCachedAt | AbsencesData, AbsencesCachedAt | AbsencesTTL |
| ScrapeDifferentGrades | GradesDifferentData | GradesDifferentCachedAt | both | GradesDifferentTTL |

**Note on ScrapeSpecificSubjectGrades:** This endpoint takes a `subjectId` parameter. Use the `SpecificSubjectGradesJson` column on `StudentCache` — a JSON-serialized `Dictionary<string, string>` keyed by `subjectId`. On cache hit, deserialize the dict and return the entry for the requested `subjectId`. On cache miss, scrape, update the dict, and re-serialize. Use the same `GradesTTL` (2 hours). The `LastForceRefreshAt` cooldown applies here too — a force refresh on this endpoint resets the same cooldown field as the full grades endpoint.

---

## Task 12: Build the GradeChangeDetectionService

This runs after every successful grade fetch (both when a user opens the app and during the background job). It compares the current grades to the stored `GradeSnapshots` and triggers task generation + FCM notification when a significant drop is detected.

**A drop triggers when ALL of these are true:**
- The subject is in the student's `MonitoredSubjects` list
- The student's 3-month average for that subject was above 2.5
- The newest grade is ≥ 1.5 points below that average

- [ ] Create `Services/GradeChangeDetectionService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using YourApp.Database;
using YourApp.Database.Models;

namespace YourApp.Services;

public class GradeChangeDetectionService
{
    private readonly AppDbContext _db;
    private readonly TaskGenerationService _taskGen;
    private readonly FcmService _fcm; // your existing FCM notification sender

    private const decimal DropThreshold = 1.5m;
    private const decimal MinAverageToTrigger = 2.5m;

    public GradeChangeDetectionService(
        AppDbContext db,
        TaskGenerationService taskGen,
        FcmService fcm)
    {
        _db = db;
        _taskGen = taskGen;
        _fcm = fcm;
    }

    // Call this after every grade fetch. Runs async in background — does not block the user response.
    public async Task CheckForDrops(string email, GradesResponse currentGrades)
    {
        var monitored = await _db.MonitoredSubjects
            .Where(m => m.Email == email)
            .ToListAsync();

        if (!monitored.Any()) return;

        foreach (var subject in currentGrades.Subjects)
        {
            var isMonitored = monitored.Any(m => m.SubjectId == subject.SubjectId);
            if (!isMonitored) continue;

            var snapshot = await _db.GradeSnapshots
                .FindAsync(email, subject.SubjectId);

            if (snapshot == null)
            {
                // First time seeing this subject — store baseline, no notification yet
                _db.GradeSnapshots.Add(new GradeSnapshot
                {
                    Email = email,
                    SubjectId = subject.SubjectId,
                    SubjectName = subject.SubjectName,
                    LastKnownAverage = subject.Average,
                    LastKnownLatestGrade = subject.LatestGrade,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                var isDrop = subject.LatestGrade > 0
                    && snapshot.LastKnownAverage > MinAverageToTrigger
                    && snapshot.LastKnownAverage - subject.LatestGrade >= DropThreshold
                    && subject.LatestGrade != snapshot.LastKnownLatestGrade; // new grade, not seen before

                if (isDrop)
                {
                    await HandleDrop(email, subject.SubjectId, subject.SubjectName,
                                     subject.LatestGrade, snapshot.LastKnownAverage);
                }

                snapshot.LastKnownAverage = subject.Average;
                snapshot.LastKnownLatestGrade = subject.LatestGrade;
                snapshot.UpdatedAt = DateTime.UtcNow;
                _db.GradeSnapshots.Update(snapshot);
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task HandleDrop(
        string email, string subjectId, string subjectName,
        decimal newGrade, decimal average)
    {
        // Generate practice tasks via OpenAI
        var tasks = await _taskGen.GenerateTasks(subjectName, newGrade, average);

        // Save task set to database
        await _db.TaskSets.AddAsync(new TaskSet
        {
            Email = email,
            SubjectId = subjectId,
            SubjectName = subjectName,
            TasksJson = System.Text.Json.JsonSerializer.Serialize(tasks),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        // Send FCM push notification
        await _fcm.SendNotification(
            email: email,
            title: $"Novi zadaci — {subjectName}",
            body: $"Dobio si nižu ocjenu. Pripremili smo {tasks.Count} zadataka koji ti mogu pomoći da se popraviš."
        );
    }
}
```

---

## Task 13: Build the background job endpoint

Called by a Firebase Cloud Function 2x per day at 15:00 and 22:00 Zagreb time. Protected by a secret header. NOT called by the Flutter app.

**What the background job does:**
- Finds all users who have monitored subjects AND were active in the last 30 days
- For each user: attempts a live CARNET grades scrape using their cached session token
- If the session is still valid: scrapes fresh grades, updates Postgres cache, runs grade change detection
- If the session is expired (Heroku restarted, token wiped): sends a gentle "check your grades" reminder instead — does not attempt to re-authenticate (we don't store passwords)
- 500ms delay between users to respect CARNET rate limits

- [ ] Create `Controllers/BackgroundController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using YourApp.Database;
using YourApp.Services;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BackgroundController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GradeChangeDetectionService _detector;
    private readonly FcmService _fcm;
    private readonly ICarnetScraper _scraper;
    private readonly IConfiguration _config;

    public BackgroundController(
        AppDbContext db,
        GradeChangeDetectionService detector,
        FcmService fcm,
        ICarnetScraper scraper,
        IConfiguration config)
    {
        _db = db;
        _detector = detector;
        _fcm = fcm;
        _scraper = scraper;
        _config = config;
    }

    [HttpPost("CheckNewGrades")]
    public async Task<IActionResult> CheckNewGrades(
        [FromHeader(Name = "X-Background-Secret")] string secret)
    {
        if (secret != _config["BackgroundJobSecret"])
            return Unauthorized();

        var usersWithMonitoring = await _db.MonitoredSubjects
            .Join(_db.StudentCache,
                  m => m.Email,
                  c => c.Email,
                  (m, c) => new { m.Email, c.ActiveToken, c.TokenStoredAt, c.LastActiveAt })
            .Where(x => x.LastActiveAt > DateTime.UtcNow.AddDays(-30))
            .Select(x => new { x.Email, x.ActiveToken, x.TokenStoredAt, x.LastActiveAt })
            .Distinct()
            .ToListAsync();

        int scraped = 0;
        int remindersSent = 0;
        int failed = 0;

        foreach (var user in usersWithMonitoring)
        {
            try
            {
                // Token stored within the last 23h means session is likely still valid
                var tokenAge = user.TokenStoredAt != null
                    ? DateTime.UtcNow - user.TokenStoredAt.Value
                    : TimeSpan.MaxValue;

                var hasValidSession = user.ActiveToken != null && tokenAge < TimeSpan.FromHours(23);

                if (hasValidSession)
                {
                    // Attempt live CARNET scrape using the cached session
                    try
                    {
                        var grades = await _scraper.ScrapeGradesWithToken(user.ActiveToken!);

                        // Update Postgres cache
                        var cache = await _db.StudentCache.FindAsync(user.Email);
                        if (cache != null)
                        {
                            cache.GradesData = JsonSerializer.Serialize(grades);
                            cache.GradesCachedAt = DateTime.UtcNow;
                            cache.LastActiveAt = DateTime.UtcNow;
                            _db.StudentCache.Update(cache);
                            await _db.SaveChangesAsync();
                        }

                        // Run grade change detection on fresh data
                        await _detector.CheckForDrops(user.Email, grades);
                        scraped++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Session expired mid-job — clear the token, send reminder
                        var cache = await _db.StudentCache.FindAsync(user.Email);
                        if (cache != null) { cache.ActiveToken = null; _db.StudentCache.Update(cache); await _db.SaveChangesAsync(); }
                        await SendReminder(user.Email);
                        remindersSent++;
                    }
                }
                else
                {
                    // No valid session — send reminder only if they haven't opened the app today
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
                Console.Error.WriteLine($"[Background] Failed for {user.Email}: {ex.Message}");
                failed++;
            }
        }

        return Ok(new { scraped, remindersSent, failed, total = usersWithMonitoring.Count });
    }

    private async Task SendReminder(string email)
    {
        await _fcm.SendNotification(
            email: email,
            title: "Provjeri svoje ocjene",
            body: "Otvori Odlikas i provjeri ima li novih ocjena danas.");
    }
}
```

> **Backend dependency:** `ICarnetScraper` needs a `ScrapeGradesWithToken(string token)` method that accepts an already-authenticated session token instead of email/password. Add this overload to your existing scraper implementation.

- [ ] Add to Heroku config:

```bash
heroku config:set BackgroundJobSecret="generate-a-long-random-string-here" --app your-app-name
```

---

## Task 14: Build TaskGenerationService (OpenAI)

Generates practice tasks in Croatian when a grade drop is detected.

- [ ] Create `Services/TaskGenerationService.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace YourApp.Services;

public class TaskGenerationService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;

    public TaskGenerationService(HttpClient http, IConfiguration config)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"]!;
    }

    public async Task<List<string>> GenerateTasks(
        string subjectName,
        decimal badGrade,
        decimal subjectAverage,
        int count = 7)
    {
        var prompt = $"""
            Ti si pomoćnik za učenje za hrvatske učenike srednje škole.
            Učenik je iz predmeta "{subjectName}" dobio ocjenu {badGrade:F1},
            a njegov dosadašnji prosjek iz tog predmeta je {subjectAverage:F1}.
            Generiraj {count} konkretnih zadataka za vježbu koji će mu pomoći
            da bolje razumije gradivo i popravi ocjenu.
            Zadaci trebaju biti na hrvatskom jeziku, prilagođeni razini srednje škole.
            Formatiraj kao numeriranu listu. Samo zadaci, bez rješenja.
            """;

        var requestBody = new
        {
            model = "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = "Ti si iskusni nastavnik koji pomaže učenicima srednje škole." },
                new { role = "user", content = prompt }
            },
            max_tokens = 1000,
            temperature = 0.7
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var content = result
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Trim())
            .ToList();
    }
}
```

- [ ] Register in `Program.cs`:

```csharp
builder.Services.AddHttpClient<TaskGenerationService>();
builder.Services.AddScoped<GradeChangeDetectionService>();
builder.Services.AddScoped<TaskGenerationService>();
```

---

## Task 15: New endpoints for Flutter app features

Add a `PomodoroController` and a `NotificationsController` (separate from the existing FCM notifications — these are for smart task notifications).

- [ ] Create `Controllers/PomodoroController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YourApp.Database;
using YourApp.Database.Models;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize] // requires Bearer token, same as your other controllers
public class PomodoroController : ControllerBase
{
    private readonly AppDbContext _db;

    public PomodoroController(AppDbContext db)
    {
        _db = db;
    }

    // Called when a student completes a 25-minute Pomodoro session.
    // The Flutter app is responsible for enforcing the 25-minute minimum —
    // the backend trusts it and records the session.
    [HttpPost("CompleteSession")]
    public async Task<IActionResult> CompleteSession()
    {
        var email = GetEmailFromToken();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var session = await _db.PomodoroSessions
            .FirstOrDefaultAsync(s => s.Email == email && s.SessionDate == today);

        if (session == null)
        {
            session = new PomodoroSession { Email = email, SessionDate = today };
            _db.PomodoroSessions.Add(session);
        }

        if (session.SessionsCompleted >= 8)
            return Ok(new { streak = await CalculateStreak(email), capped = true });

        session.SessionsCompleted++;
        session.TotalMinutes += 25;
        await _db.SaveChangesAsync();

        return Ok(new { streak = await CalculateStreak(email), capped = false });
    }

    [HttpGet("GetStreak")]
    public async Task<IActionResult> GetStreak()
    {
        var email = GetEmailFromToken();
        return Ok(await CalculateStreak(email));
    }

    private async Task<object> CalculateStreak(string email)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var sessions = await _db.PomodoroSessions
            .Where(s => s.Email == email && s.SessionDate <= today)
            .OrderByDescending(s => s.SessionDate)
            .Take(365)
            .ToListAsync();

        int currentStreak = 0;
        var checkDate = today;

        foreach (var session in sessions)
        {
            if (session.SessionDate == checkDate && session.SessionsCompleted > 0)
            {
                currentStreak++;
                checkDate = checkDate.AddDays(-1);
            }
            else if (session.SessionDate < checkDate)
            {
                break;
            }
        }

        // Longest streak: scan all sessions
        int longestStreak = 0;
        int tempStreak = 0;
        DateOnly? prev = null;
        foreach (var session in sessions.OrderBy(s => s.SessionDate))
        {
            if (session.SessionsCompleted == 0) continue;
            if (prev == null || session.SessionDate == prev.Value.AddDays(1))
            {
                tempStreak++;
                longestStreak = Math.Max(longestStreak, tempStreak);
            }
            else
            {
                tempStreak = 1;
            }
            prev = session.SessionDate;
        }

        return new
        {
            currentStreak,
            longestStreak,
            todaySessions = sessions.FirstOrDefault(s => s.SessionDate == today)?.SessionsCompleted ?? 0,
            todayMinutes = sessions.FirstOrDefault(s => s.SessionDate == today)?.TotalMinutes ?? 0
        };
    }

    private string GetEmailFromToken()
    {
        // Read the existing scraper controllers before implementing — they already have
        // the token-to-email resolution logic via SessionStore.
        // Extract it into ApiBaseController that all authenticated controllers inherit from.
        // This stub must be replaced before this controller compiles.
        throw new NotImplementedException("Extract from existing scraper controllers into ApiBaseController");
    }
}
```

- [ ] Create `Controllers/StudyNotificationsController.cs` (smart task notifications, separate from FCM push notifications):

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using YourApp.Database;
using YourApp.Database.Models;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StudyNotificationsController : ControllerBase
{
    private readonly AppDbContext _db;

    public StudyNotificationsController(AppDbContext db)
    {
        _db = db;
    }

    // Student sets which subject(s) to monitor for grade drop notifications.
    // Free tier: max 1 subject. Odlikas+ premium: max 5.
    [HttpPost("SetMonitoredSubjects")]
    public async Task<IActionResult> SetMonitoredSubjects(
        [FromBody] List<MonitoredSubjectDto> subjects)
    {
        var email = GetEmailFromToken();
        const int maxFree = 1;
        const int maxPremium = 5;
        var cache = await _db.StudentCache.FindAsync(email);
        var max = (cache?.IsOdlikasPlus == true) ? maxPremium : maxFree;

        // 403 — never trust client-side gating alone. Flutter enforces this in the UI,
        // but the backend is the authoritative gate regardless of what the client sends.
        if (subjects.Count > max)
            return StatusCode(403, new { error = $"Besplatni plan dozvoljava praćenje {max} predmeta. Nadogradi na Odlikas+ za praćenje do 5 predmeta." });

        var existing = await _db.MonitoredSubjects
            .Where(m => m.Email == email).ToListAsync();
        _db.MonitoredSubjects.RemoveRange(existing);

        foreach (var s in subjects.Take(max))
        {
            _db.MonitoredSubjects.Add(new MonitoredSubject
            {
                Email = email,
                SubjectId = s.SubjectId,
                SubjectName = s.SubjectName
            });
        }

        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("GetMonitoredSubjects")]
    public async Task<IActionResult> GetMonitoredSubjects()
    {
        var email = GetEmailFromToken();
        var subjects = await _db.MonitoredSubjects
            .Where(m => m.Email == email)
            .Select(m => new { m.SubjectId, m.SubjectName })
            .ToListAsync();
        return Ok(subjects);
    }

    // Returns pending (uncompleted) task sets for this student.
    [HttpGet("GetPendingTasks")]
    public async Task<IActionResult> GetPendingTasks()
    {
        var email = GetEmailFromToken();
        var taskSets = await _db.TaskSets
            .Where(t => t.Email == email && !t.IsCompleted)
            .OrderByDescending(t => t.CreatedAt)
            .Take(5)
            .ToListAsync();

        return Ok(taskSets.Select(t => new
        {
            t.Id,
            t.SubjectName,
            tasks = JsonSerializer.Deserialize<List<string>>(t.TasksJson),
            t.CreatedAt
        }));
    }

    // Student marks a task set as completed.
    // Completing a task set counts as a study session — increments streak.
    [HttpPost("CompleteTaskSet/{id:int}")]
    public async Task<IActionResult> CompleteTaskSet(int id)
    {
        var email = GetEmailFromToken();
        var taskSet = await _db.TaskSets.FindAsync(id);

        if (taskSet == null || taskSet.Email != email)
            return NotFound();

        if (taskSet.IsCompleted)
            return Ok(new { alreadyCompleted = true });

        taskSet.IsCompleted = true;
        taskSet.CompletedAt = DateTime.UtcNow;

        // Count as a study session toward streak
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var session = await _db.PomodoroSessions
            .FirstOrDefaultAsync(s => s.Email == email && s.SessionDate == today);

        if (session == null)
        {
            session = new PomodoroSession
            {
                Email = email,
                SessionDate = today,
                SessionsCompleted = 1,
                TotalMinutes = 0 // task completion, not timed
            };
            _db.PomodoroSessions.Add(session);
        }
        else if (session.SessionsCompleted < 8)
        {
            session.SessionsCompleted++;
        }

        await _db.SaveChangesAsync();
        return Ok(new { success = true });
    }

    private string GetEmailFromToken()
    {
        throw new NotImplementedException("Use your existing token-to-email logic");
    }
}

public record MonitoredSubjectDto(string SubjectId, string SubjectName);
```

---

## Task 16: Leaderboard endpoints

- [ ] Create `Controllers/LeaderboardController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using YourApp.Database;
using YourApp.Database.Models;

namespace YourApp.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LeaderboardController : ControllerBase
{
    private readonly AppDbContext _db;

    public LeaderboardController(AppDbContext db)
    {
        _db = db;
    }

    // Student opts into the leaderboard by choosing a nickname.
    // Their class/city/county is populated from their e-Dnevnik profile.
    [HttpPost("OptIn")]
    public async Task<IActionResult> OptIn([FromBody] OptInDto dto)
    {
        var email = GetEmailFromToken();

        if (string.IsNullOrWhiteSpace(dto.Nickname) || dto.Nickname.Length > 50)
            return BadRequest(new { error = "Nickname mora biti između 1 i 50 znakova." });

        var nicknameInUse = await _db.LeaderboardEntries
            .AnyAsync(e => e.Nickname == dto.Nickname && e.Email != email);
        if (nicknameInUse)
            return Conflict(new { error = "Taj nadimak je već zauzet. Odaberi drugi." });

        var entry = await _db.LeaderboardEntries.FindAsync(email)
                    ?? new LeaderboardEntry { Email = email, OptedInAt = DateTime.UtcNow };

        entry.Nickname = dto.Nickname;
        entry.ClassId = dto.ClassId;
        entry.SchoolId = dto.SchoolId;
        entry.City = dto.City;
        entry.County = dto.County;

        if (_db.LeaderboardEntries.Local.Contains(entry))
            _db.LeaderboardEntries.Update(entry);
        else
            _db.LeaderboardEntries.Add(entry);

        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("OptOut")]
    public async Task<IActionResult> OptOut()
    {
        var email = GetEmailFromToken();
        var entry = await _db.LeaderboardEntries.FindAsync(email);
        if (entry != null)
        {
            _db.LeaderboardEntries.Remove(entry);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    // Returns class leaderboard. Only shows nickname + score — never real name or raw grades.
    [HttpGet("Class/{classId}")]
    public async Task<IActionResult> GetClassLeaderboard(string classId)
    {
        var entries = await _db.LeaderboardEntries
            .Where(e => e.ClassId == classId)
            .OrderByDescending(e => e.CombinedScore)
            .Take(50)
            .Select(e => new
            {
                e.Nickname,
                e.CombinedScore,
                e.CurrentStreak,
                e.GradeDeltaScore,
                e.StreakScore
            })
            .ToListAsync();

        return Ok(entries);
    }

    // Recalculates all leaderboard scores. Called by a separate scheduled job weekly.
    // Protected by the same background job secret.
    [HttpPost("RecalculateScores")]
    public async Task<IActionResult> RecalculateScores(
        [FromHeader(Name = "X-Background-Secret")] string secret,
        [FromServices] IConfiguration config)
    {
        if (secret != config["BackgroundJobSecret"])
            return Unauthorized();

        var entries = await _db.LeaderboardEntries.ToListAsync();
        // GradeBaseline has composite key (Email, SchoolYear) — filter to current year only,
        // otherwise students who have baselines from multiple years cause duplicate key errors.
        var currentSchoolYear = GetCurrentSchoolYear();
        var baselines = await _db.GradeBaselines
            .Where(b => b.SchoolYear == currentSchoolYear)
            .ToDictionaryAsync(b => b.Email);
        var sessions = await _db.PomodoroSessions.ToListAsync();

        // For each entry, calculate grade delta and streak score
        foreach (var entry in entries)
        {
            // Grade delta: current average - baseline average
            // Current average must come from cached grades
            var cache = await _db.StudentCache.FindAsync(entry.Email);
            if (cache?.GradesData == null || !baselines.TryGetValue(entry.Email, out var baseline))
                continue;

            var grades = System.Text.Json.JsonSerializer.Deserialize<GradesResponse>(cache.GradesData);
            if (grades == null) continue;

            var currentAverage = grades.Subjects.Average(s => s.Average);
            var delta = (decimal)currentAverage - baseline.BaselineAverage;

            // Streak: count current streak from PomodoroSessions
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var userSessions = sessions
                .Where(s => s.Email == entry.Email)
                .OrderByDescending(s => s.SessionDate)
                .ToList();

            int streak = 0;
            var checkDate = today;
            foreach (var s in userSessions)
            {
                if (s.SessionDate == checkDate && s.SessionsCompleted > 0) { streak++; checkDate = checkDate.AddDays(-1); }
                else if (s.SessionDate < checkDate) break;
            }

            entry.CurrentStreak = streak;
            entry.GradeDeltaScore = delta; // raw delta, normalization happens client-side or in a second pass
            entry.StreakScore = Math.Min(streak, 30); // cap at 30 for scoring
            entry.CombinedScore = (entry.GradeDeltaScore * 0.6m) + (entry.StreakScore / 30m * 100m * 0.4m);
            entry.LastScoreUpdate = DateTime.UtcNow;

            _db.LeaderboardEntries.Update(entry);
        }

        await _db.SaveChangesAsync();
        return Ok(new { updated = entries.Count });
    }

    // Called once at school year start to snapshot current grade averages as baselines.
    [HttpPost("SetBaselines")]
    public async Task<IActionResult> SetBaselines(
        [FromHeader(Name = "X-Background-Secret")] string secret,
        [FromServices] IConfiguration config)
    {
        if (secret != config["BackgroundJobSecret"])
            return Unauthorized();

        var allCache = await _db.StudentCache
            .Where(c => c.GradesData != null)
            .ToListAsync();

        var schoolYear = GetCurrentSchoolYear();
        int set = 0;

        foreach (var cache in allCache)
        {
            // GradeBaseline has composite key (Email, SchoolYear) — pass both values to FindAsync
            var existing = await _db.GradeBaselines.FindAsync(cache.Email, schoolYear);
            if (existing != null) continue; // don't overwrite existing baseline for this year

            var grades = System.Text.Json.JsonSerializer.Deserialize<GradesResponse>(cache.GradesData!);
            if (grades == null || !grades.Subjects.Any()) continue;

            var average = (decimal)grades.Subjects.Average(s => s.Average);
            _db.GradeBaselines.Add(new GradeBaseline
            {
                Email = cache.Email,
                SchoolYear = schoolYear,
                BaselineAverage = average,
                RecordedAt = DateTime.UtcNow
            });
            set++;
        }

        await _db.SaveChangesAsync();
        return Ok(new { baselinesSet = set });
    }

    private static string GetCurrentSchoolYear()
    {
        var now = DateTime.UtcNow;
        var year = now.Month >= 9 ? now.Year : now.Year - 1;
        return $"{year}-{year + 1}";
    }

    private string GetEmailFromToken()
    {
        throw new NotImplementedException("Use your existing token-to-email logic");
    }
}

public record OptInDto(string Nickname, string ClassId, string SchoolId, string City, string County);
```

---

## Task 17: Firebase Cloud Function scheduler

In the `functions/` folder of the Firebase project:

- [ ] Install axios if not already present:

```bash
cd functions && npm install axios
```

- [ ] Create `functions/src/backgroundJobs.ts`:

```typescript
import * as functions from "firebase-functions";
import axios from "axios";

const BACKEND_URL = functions.config().backend.url;
const SECRET = functions.config().backend.job_secret;

const headers = { "X-Background-Secret": SECRET };

// Grade check — 2x per day: 15:00 (catches morning grades) and 22:00 (catches afternoon grades)
export const checkNewGrades = functions.pubsub
    .schedule("0 15,22 * * *")
    .timeZone("Europe/Zagreb")
    .onRun(async () => {
        const res = await axios.post(
            `${BACKEND_URL}/api/Background/CheckNewGrades`,
            {},
            { headers, timeout: 540_000 }
        );
        console.log("Grade check result:", res.data);
    });

// Leaderboard recalculation — once per week, Sunday at 23:00
export const recalculateLeaderboard = functions.pubsub
    .schedule("0 23 * * 0")
    .timeZone("Europe/Zagreb")
    .onRun(async () => {
        const res = await axios.post(
            `${BACKEND_URL}/api/Leaderboard/RecalculateScores`,
            {},
            { headers, timeout: 300_000 }
        );
        console.log("Leaderboard recalc result:", res.data);
    });
```

- [ ] Set config values:

```bash
firebase functions:config:set backend.url="https://your-heroku-app.herokuapp.com"
firebase functions:config:set backend.job_secret="your-secret-here"
```

- [ ] Set the same secret on Heroku:

```bash
heroku config:set BackgroundJobSecret="your-secret-here" --app your-app-name
```

- [ ] Deploy:

```bash
cd functions && npm run build && firebase deploy --only functions
```

---

## Task 18: Deploy and verify

- [ ] Run migrations on Heroku:

```bash
heroku run "dotnet ef database update" --app your-app-name
```

- [ ] Deploy backend:

```bash
git push heroku main
```

- [ ] Test caching manually with Postman:
  - Call `GET /api/Scraper/ScrapeSubjectsAndProfessors` twice
  - Second response should have `isFromCache: true` and return instantly
  - Check Heroku logs — second call should not show any CARNET activity

- [ ] Test force refresh rate limiting:
  - Call with `?forceRefresh=true` twice within 15 minutes
  - Second call should return cached data with `isFromCache: true`

- [ ] Test background job:
  - Call `POST /api/Background/CheckNewGrades` with correct `X-Background-Secret` header
  - Should return `{ checkedFromCache: N, remindersent: M, skipped: K, total: T }`
  - Without the header: should return 401

- [ ] Verify Firebase Functions appear in Firebase Console under Functions tab

---

## Definition of Done

- [ ] All 7 Postgres tables exist in Heroku
- [ ] Login endpoint stores `ActiveToken` and `LastActiveAt` in `StudentCache`, returns `firebaseToken`
- [ ] `POST /api/Device/RegisterToken` stores FCM token in `StudentCache.FcmToken` — no Firestore writes for FCM tokens anywhere in the backend
- [ ] Every scraper endpoint: returns cached data on repeat requests within TTL, returns `{ data, cachedAt, isFromCache }` shape
- [ ] `ScrapeSpecificSubjectGrades` caches per-subjectId in `SpecificSubjectGradesJson` column
- [ ] Force refresh rate limited to once per 15 minutes per student
- [ ] Background job endpoint: protected by secret, attempts live CARNET scrape for users with valid sessions, sends reminder for expired sessions, returns `{ scraped, remindersSent, failed, total }`
- [ ] Firebase Cloud Functions: `checkNewGrades` (2x/day at 15:00 and 22:00 Zagreb) and `recalculateLeaderboard` (weekly) deployed and visible in Firebase Console
- [ ] Pomodoro `CompleteSession` endpoint: records session, caps at 8/day, returns streak data
- [ ] `SetMonitoredSubjects` enforces limit server-side (403 if free tier exceeds 1 subject), Postgres is source of truth
- [ ] `GetPendingTasks`, `CompleteTaskSet` endpoints exist and work
- [ ] Leaderboard `OptIn`, `OptOut`, `GetClassLeaderboard` endpoints exist
- [ ] `SetBaselines` endpoint exists (run manually once at school year start)
- [ ] `GetEmailFromToken()` extracted into `ApiBaseController`, not duplicated per controller
- [ ] All endpoints tested in Postman before Flutter integration begins

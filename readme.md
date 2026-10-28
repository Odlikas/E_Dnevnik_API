# E-Dnevnik API

Backend REST API for the **Odlikaš** mobile app — a companion for the Croatian national e-grade system [E-Dnevnik](https://ocjene.skole.hr). The API authenticates students, scrapes their grade data, detects new grades in real time and delivers push notifications via Firebase Cloud Messaging.

**Live:** `https://e-dnevnik-api.fly.dev`

---

## Features

- Student login via E-Dnevnik credentials (session-based, stored server-side)
- Grade scraping: all subjects, individual subject detail, grade history
- Absence tracking
- Upcoming test schedule
- Student profile and school class info
- New grades and new tests since last check (fetched on demand, cached 10 min per session)
- Twice-daily background grade check (15:00 and 22:00, triggered by a Firebase function) with 500 ms between users
- Push notifications on grade drop (FCM)
- Leaderboard — opt-in anonymous GPA ranking by school program
- Pomodoro session tracking
- Study notification scheduling
- Stripe subscription paywall — create subscription, confirm payment, cancel
- Rate limiting (20 req/min per IP), HSTS, security headers
- Auto-migration on startup (EF Core + PostgreSQL)

---

## Tech Stack

| Layer | Technology |
|---|---|
| Runtime | .NET 8 (ASP.NET Core) |
| Database | PostgreSQL + Entity Framework Core 8 |
| Scraping | HtmlAgilityPack |
| Push notifications | Firebase Admin SDK (FCM) |
| Payments | Stripe.net v51 |
| Realtime DB | Google.Cloud.Firestore |
| Deployment | Docker → Fly.io |
| DB hosting | Neon (serverless Postgres) |

---

## Project Structure

```
E_Dnevnik_API/
├── Controllers/          # HTTP endpoints (thin layer, no business logic)
│   ├── LoginController               # POST /api/Login, DELETE /api/Login
│   ├── ScraperController             # All scraping endpoints
│   ├── AccountController             # Account status, Odlikas+ flag
│   ├── LeaderboardController         # Anonymous GPA leaderboard
│   ├── DeviceController              # FCM token registration
│   ├── BackgroundController          # Manual background refresh trigger
│   ├── PomodoroController            # Pomodoro session tracking and streaks
│   ├── StudyNotificationsController  # Monitored subjects, AI task sets
│   └── PaymentController             # Stripe subscription (create, confirm, cancel)
├── ScrapingServices/     # E-Dnevnik HTML scraping logic
│   ├── ScraperService                # Main grades scraper
│   ├── SpecificSubjectScraperService
│   ├── StudentProfileScraperService
│   ├── AbsenceScraperService
│   ├── ScheduleTableScraperService
│   ├── DifferentGradeLinkScraperService  # Full grade history across all school years
│   ├── NewGradesScraperService       # Delta detection (new grades only)
│   ├── NewTestsScraperService
│   ├── EduHrLoginService             # CSRF-aware login, class activation
│   ├── CarnetHttp                    # Shared HttpClient factory for ocjene.skole.hr (User-Agent, timeout, request counting)
│   ├── CarnetRequestStats            # In-memory request counters (counts only, no PII)
│   ├── CarnetBackoff                 # Stop background runs on 429 / 5xx / timeout
│   ├── SessionStore                  # In-memory session management
│   └── LoginBruteForceProtection
├── Services/             # Application services
│   ├── CacheService                  # DB-backed response caching with TTLs
│   ├── FcmService                    # Firebase push notifications
│   ├── GradeChangeDetectionService   # Detects grade drops, triggers task generation
│   ├── TaskGenerationService         # OpenAI-generated study tasks on grade drop
│   └── CarnetRequestStatsReporter    # Logs a daily summary of requests made to e-Dnevnik
├── E_Dnevnik_API.Tests/  # xUnit test suite
│   ├── Unit/
│   │   ├── BruteForceProtectionTests
│   │   ├── CacheServiceTests
│   │   ├── CarnetGoodCitizenTests
│   │   └── PomodoroCapTests
│   └── Scrapers/
│       ├── SubjectScraperTests
│       ├── AbsenceScraperTests
│       └── ProfileScraperTests
├── Database/             # EF Core DbContext
├── Models/               # Response/request DTOs
├── Migrations/           # EF Core migrations (auto-applied on startup)
├── Program.cs            # App bootstrap, DI registration, middleware pipeline
└── Dockerfile
```

---

## Running Locally

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- PostgreSQL instance (local or [Neon](https://neon.tech) free tier)

### Setup

1. Clone the repo:
   ```bash
   git clone https://github.com/Karlo11111/E_Dnevnik_API.git
   cd E_Dnevnik_API
   ```

2. Add a local connection string in `appsettings.Development.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=ednevnik;Username=postgres;Password=yourpassword"
     }
   }
   ```

3. Run and start:
   ```bash
   dotnet run
   ```

4. Swagger UI available at: `http://localhost:5168/swagger`

### Environment Variables

| Variable | Required | Description |
|---|---|---|
| `DATABASE_URL` | Yes (production) | Postgres connection URL (`postgres://user:pass@host/db`) |
| `PORT` | No | Port to listen on (default: `5168`) |
| `FIREBASE_SERVICE_ACCOUNT_JSON` | No | Firebase service account JSON content for push notifications and Firestore |
| `STRIPE_SECRET_KEY` | Yes (payments) | Stripe secret key — app throws on startup if missing |

---

## Running with Docker

```bash
docker build -t e-dnevnik-api .
docker run -p 8080:8080 \
  -e DATABASE_URL="postgres://user:pass@host/db" \
  -e PORT=8080 \
  e-dnevnik-api
```

---

## API Overview

All scraping endpoints accept an optional `?forceRefresh=true` query parameter to bypass the cache (subject to a 15-minute cooldown).

| Method | Endpoint | Description |
|---|---|---|
| POST | `/api/Login` | Authenticate with E-Dnevnik credentials |
| DELETE | `/api/Login` | Logout, invalidates session token |
| GET | `/api/Scraper/ScrapeSubjectsAndProfessors` | All subjects and current grades |
| GET | `/api/Scraper/ScrapeSpecificSubjectGrades?subjectId=` | Individual subject grade detail |
| GET | `/api/Scraper/ScrapeStudentProfile` | Student profile and school class info |
| GET | `/api/Scraper/ScrapeTests` | Upcoming test schedule |
| GET | `/api/Scraper/ScrapeAbsences` | Absence records |
| GET | `/api/Scraper/ScrapeScheduleTable` | Weekly class schedule |
| GET | `/api/Scraper/ScrapeDifferentGrades` | Full grade history across all school years |
| GET | `/api/Scraper/ScrapeNewGrades` | Only grades added since last check |
| GET | `/api/Scraper/ScrapeNewTests` | Only tests added since last check |
| GET | `/api/Scraper/CalculateMissedClassPercentages` | Per-subject absence percentage |
| GET | `/api/Account/Status` | Account flags (`isOdlikasPlus`, since date) |
| GET | `/api/Leaderboard` | Anonymous GPA leaderboard by school program |
| POST | `/api/Device/token` | Register FCM device token for push notifications |
| POST | `/api/Pomodoro/CompleteSession` | Record a completed 25-min Pomodoro session |
| GET | `/api/Pomodoro/GetStreak` | Current and longest Pomodoro streak |
| POST | `/api/StudyNotifications/SetMonitoredSubjects` | Set subjects to monitor for grade drops |
| GET | `/api/StudyNotifications/GetMonitoredSubjects` | Get currently monitored subjects |
| GET | `/api/StudyNotifications/GetPendingTasks` | Get AI-generated study task sets |
| POST | `/api/StudyNotifications/CompleteTaskSet/{id}` | Mark a task set as completed |
| POST | `/api/Payment/CreateSubscription` | Start Stripe subscription, returns `clientSecret` |
| POST | `/api/Payment/Confirm` | Confirm payment, grants Odlikaš+ in Postgres + Firestore |
| POST | `/api/Payment/Cancel` | Cancel active Stripe subscription, revokes Odlikaš+ |

Full API documentation: [`API_DOCUMENTATION.md`](API_DOCUMENTATION.md)

---

## Deployment

The app is deployed on [Fly.io](https://fly.io) using the included `Dockerfile`. Database is hosted on [Neon](https://neon.tech).

EF Core migrations run automatically on startup — no manual migration step needed.

To redeploy manually:
```bash
fly deploy -a e-dnevnik-api
```

---

## Tests

Unit tests live in `E_Dnevnik_API.Tests/`. Run them with:

```bash
dotnet test
```

| Suite | Coverage |
|---|---|
| `Unit/BruteForceProtectionTests` | IsBlocked after 5 failures, case-insensitive matching, per-email isolation, reset on success |
| `Unit/CacheServiceTests` | Fresh cache hit (no fetch), stale cache miss, force-refresh within cooldown (suppressed), force-refresh after cooldown (allowed) |
| `Unit/CarnetGoodCitizenTests` | User-Agent and timeout on created clients, request counting by source, unique-user counting, counter reset, backoff decisions (429/5xx/timeout vs 4xx), daily-summary scheduling across DST |
| `Unit/PomodoroCapTests` | 8th session succeeds, 9th session returns `capped: true`, yesterday's cap does not block today |
| `Scrapers/SubjectScraperTests` | HTML parsing of subject names, professors, grades, subject IDs; `ExtractNumbers` against real href patterns |
| `Scrapers/AbsenceScraperTests` | Absence record parsing from fixture HTML |
| `Scrapers/ProfileScraperTests` | Student profile field extraction from fixture HTML |

## CI/CD

GitHub Actions (`.github/workflows/ci.yml`) runs on every push and pull request:

1. `dotnet restore` → `dotnet build` → `dotnet test`
2. On merge to `main`: auto-deploys to Fly.io via `flyctl deploy`

## Security

- Credentials never stored — only a session token tied to the live E-Dnevnik cookie
- All secrets managed via environment variables (no hardcoded values)
- Brute force protection: 5 failed login attempts → 15-minute lockout per email
- Rate limiting: 20 requests/min per IP with a `429` JSON response
- HSTS enforced in production (365-day max-age, includeSubDomains)
- Security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Content-Security-Policy: default-src 'none'`, `X-Permitted-Cross-Domain-Policies: none`
- CORS blocks all browser origins in production (API is mobile-only)

### Traffic to e-Dnevnik (`ocjene.skole.hr`)

CARNET has not formally approved or rejected scraping, so the API tries to keep its load on e-Dnevnik low and visible:

- Every request carries the User-Agent `Odlikas/1.0 (+https://odlikas.hr; info@odlikas.hr)`. All clients are built by `CarnetHttp.CreateClient`.
- No continuous polling. Data is fetched when the user asks for it and cached (profile and schedule 7 d, grades 2 h, tests and absences 1 h, grade history 24 h, new grades and new tests 10 min). Force refresh has a 15-minute cooldown.
- The only scheduled job is `POST /api/Background/CheckNewGrades`, called twice a day (15:00 and 22:00) by a Firebase function. It waits 500 ms between users and stops the whole run if e-Dnevnik returns `429` or `5xx` or times out.
- Outgoing requests are counted by source (login, user-triggered scrape, background job) along with cache hits. A summary line (total requests, unique users, requests per user) is logged daily at midnight Europe/Zagreb and on shutdown. Counters live only in memory and contain no emails.

---

## Contributing

### Branch strategy

Never commit directly to `main`. All work goes through a feature branch and a pull request.

Branch naming:
```
feat/<short-description>      # new feature
fix/<short-description>       # bug fix
refactor/<short-description>  # cleanup or restructure
docs/<short-description>      # documentation only
ci/<short-description>        # CI/CD changes
```

### Commit message conventions

```
feat: add leaderboard opt-in endpoint
fix: handle missing CSRF token on login page
refactor: extract score calculation into helper
docs: update environment variable table
ci: switch to flyctl for deploy step
test: add CacheService TTL tests
chore: remove committed zip archives
```

### Pull request process

1. Branch off `main`: `git checkout -b feat/my-feature`
2. Make changes, commit with the conventions above
3. Push and open a PR against `main`
4. CI runs automatically — build and all tests must pass before merging
5. Merge via GitHub (squash or merge commit, your choice)

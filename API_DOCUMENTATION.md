# E-Dnevnik API — Documentation

**Version:** 1.0
**Base URL (production):** `https://your-app.herokuapp.com`
**Base URL (local):** `http://localhost:5168`
**Protocol:** HTTPS required in production

---

## What is this API?

The E-Dnevnik API is a REST API that gives you programmatic access to data from
the Croatian school portal **ocjene.skole.hr** (e-Dnevnik). The portal has no
official public API, so this service securely logs in on your behalf, scrapes the
relevant pages, and returns clean structured JSON.

**What you can fetch:**
- Your subjects, professors, and average grades
- Detailed grade breakdown per subject (by month and evaluation element)
- Upcoming written exams
- Recently entered grades and newly added exams
- Your absences
- Your weekly timetable
- Your student profile (name, school, class, class master)
- Grades from all past school years

**What the API never does:**
- Store your password — it is used once to log in and immediately discarded
- Keep your personal data persistently — everything lives in memory only
- Access anything beyond what you would see logged into e-Dnevnik yourself

---

## How authentication works

Authentication is a two-step process: you log in once to get a token, then
include that token in every subsequent request.

### Step 1 — Login

Send your AAI e-Dnevnik credentials to the login endpoint. The API logs into
e-Dnevnik on your behalf and returns a **Bearer token**.

```
POST /api/Login
Content-Type: application/json

{
  "email": "ime.prezime@skole.hr",
  "password": "<your-ednevnik-password>"
}
```

**Successful response (200):**
```json
{
  "token": "abc123XYZ...base64encodedstring=="
}
```

**Failed responses:**

| Code | Body | Reason |
|------|------|--------|
| `400` | `"Email i lozinka moraju biti uneseni."` | Missing fields |
| `429` | `"previše neuspjelih pokušaja prijave. pokušaj ponovo za 15 minuta."` | Brute force lockout |
| `401` | `"prijava nije uspjela, provjeri email i lozinku."` | Wrong credentials |
| `500` | `"csrf token nije pronađen."` | e-Dnevnik is unreachable |

### Step 2 — Use the token

Include the token in the `Authorization` header of every request:

```
Authorization: Bearer abc123XYZ...base64encodedstring==
```

### Token lifetime

Your token is valid for **24 hours** from the last time you used it. Every
successful API call resets this timer (sliding window). If the token expires
or is invalid, any endpoint will return `401` — simply log in again to get a
new one.

### Logging out

To invalidate your session immediately on the server:

```
DELETE /api/Login
Authorization: Bearer <your-token>
```

Returns `200` on success. After this the token stops working immediately.

---

## All endpoints

---

### Subjects and professors

**`GET /api/Scraper/ScrapeSubjectsAndProfessors`**

Returns all your subjects for the current school year with the assigned professor
and your current average grade for each.

**Request:**
```
GET /api/Scraper/ScrapeSubjectsAndProfessors
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "subjects": [
    {
      "subjectName": "Matematika",
      "professorName": "Marko Marković",
      "grade": "4",
      "subjectId": "75229928950"
    },
    {
      "subjectName": "Fizika",
      "professorName": "Ana Anić",
      "grade": "5",
      "subjectId": "75229928951"
    }
  ]
}
```

> **Note:** The `subjectId` value from this response is what you pass to
> `ScrapeSpecificSubjectGrades` to get detailed grade information for that subject.

---

### Specific subject grades

**`GET /api/Scraper/ScrapeSpecificSubjectGrades?subjectId=XXX`**

Returns the full grade breakdown for a single subject: grades grouped by month,
evaluation elements (e.g. oral, written, project), and the final grade.

**Request:**
```
GET /api/Scraper/ScrapeSpecificSubjectGrades?subjectId=75229928950
Authorization: Bearer <token>
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `subjectId` | numeric string | Yes | The subject ID from the subjects list |

**Response (200):**
```json
{
  "evaluationElements": [
    {
      "name": "Usmena provjera znanja",
      "gradesByMonth": ["4", "5", "3"]
    },
    {
      "name": "Pisana provjera znanja",
      "gradesByMonth": ["4", "4"]
    }
  ],
  "monthlyGrades": [
    {
      "month": "10",
      "grades": [
        {
          "date": "10.10.",
          "note": "Derivacije",
          "grade": "4"
        }
      ]
    }
  ],
  "finalGrade": "4"
}
```

**Errors specific to this endpoint:**

| Code | Body | Reason |
|------|------|--------|
| `400` | `"Subject ID mora biti unesen."` | Missing `subjectId` parameter |
| `400` | `"Subject ID mora biti broj."` | `subjectId` contains non-numeric characters |

---

### Student profile

**`GET /api/Scraper/ScrapeStudentProfile`**

Returns personal and school information about the logged-in student.

**Request:**
```
GET /api/Scraper/ScrapeStudentProfile
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "studentProfile": {
    "studentName": "Luka Lukić",
    "studentGrade": "4.b",
    "studentSchoolYear": "2024./2025.",
    "studentSchool": "Tehnička škola",
    "studentSchoolCity": "Zagreb",
    "classMaster": "Petra Petrić",
    "studentProgram": "Tehničar za računalstvo"
  }
}
```

---

### Exams

**`GET /api/Scraper/ScrapeTests`**

Returns all upcoming and past written exams grouped by month.

**Request:**
```
GET /api/Scraper/ScrapeTests
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "sijecanj": [
    {
      "testName": "Matematika",
      "testDescription": "Integrali",
      "testDate": "15.01."
    }
  ],
  "veljaca": [
    {
      "testName": "Fizika",
      "testDescription": "Elektromagnetizam",
      "testDate": "03.02."
    }
  ]
}
```

The keys are Croatian month names (`sijecanj`, `veljaca`, `ozujak`, `travanj`,
`svibanj`, `lipanj`, `srpanj`, `kolovoz`, `rujan`, `listopad`, `studeni`, `prosinac`).

---

### New grades

**`GET /api/Scraper/ScrapeNewGrades`**

Returns grades that have been recently entered by teachers. This endpoint is
**cached server-side** for 10 minutes per session. The first call (cache miss) fetches
live from e-Dnevnik; repeat calls within 10 minutes return instantly. There is no
background polling.

**Request:**
```
GET /api/Scraper/ScrapeNewGrades
Authorization: Bearer <token>
```

**Response (200) — grades exist:**
```json
{
  "grades": [
    {
      "date": "24.03.",
      "subjectName": "Matematika",
      "description": "Pisana provjera - Integrali",
      "gradeNumber": "4",
      "elementOfEvaluation": "Pisana provjera znanja"
    }
  ]
}
```

**Response (200) — no new grades:**
```json
{
  "grades": null
}
```

---

### New tests

**`GET /api/Scraper/ScrapeNewTests`**

Returns exams that have been recently added by teachers. Also **cached server-side**
for 10 minutes per session — same caching behaviour as new grades.

**Request:**
```
GET /api/Scraper/ScrapeNewTests
Authorization: Bearer <token>
```

**Response (200) — tests exist:**
```json
{
  "tests": [
    {
      "date": "15.04.",
      "testSubject": "Fizika",
      "description": "Elektromagnetizam - test"
    }
  ]
}
```

**Response (200) — no new tests:**
```json
{
  "tests": null
}
```

---

### Absences

**`GET /api/Scraper/ScrapeAbsences`**

Returns your absences grouped by date, with each subject you missed listed
per day.

**Request:**
```
GET /api/Scraper/ScrapeAbsences
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "absences": [
    {
      "date": "12.11.2024.",
      "subjects": ["Matematika", "Fizika", "Engleski jezik"]
    },
    {
      "date": "15.11.2024.",
      "subjects": ["Tjelesna i zdravstvena kultura"]
    }
  ]
}
```

---

### Timetable

**`GET /api/Scraper/ScrapeScheduleTable`**

Returns your weekly timetable. If your school has a morning and afternoon shift,
subjects are split into `PON Morning` / `PON Afternoon` etc.

**Request:**
```
GET /api/Scraper/ScrapeScheduleTable
Authorization: Bearer <token>
```

**Response (200) — single shift:**
```json
{
  "schedule": [
    {
      "day": "PON",
      "subjects": ["Matematika", "Fizika", "Engleski jezik", "Kemija", "Tjelesna i zdravstvena kultura"]
    },
    {
      "day": "UTO",
      "subjects": ["Hrvatski jezik", "Informatika", "Matematika"]
    }
  ]
}
```

Days returned: `PON`, `UTO`, `SRI`, `ČET`, `PET`, `SUB` (only days with classes appear).

---

### All past school years

**`GET /api/Scraper/ScrapeDifferentGrades`**

Returns subjects and final grades for every school year the student has attended,
not just the current one.

> ⚠️ **This endpoint is slow.** It navigates through multiple pages on e-Dnevnik
> (one per school year, plus individual subject pages for any subject without an
> average grade). Expect 10–60 seconds depending on how many years of history exist.
> Use it sparingly and cache the result on the client side.

**Request:**
```
GET /api/Scraper/ScrapeDifferentGrades
Authorization: Bearer <token>
```

**Response (200):**
```json
[
  {
    "gradeName": "4.b",
    "subjects": [
      {
        "subjectName": "Matematika",
        "professorName": "Marko Marković",
        "grade": "4",
        "subjectId": "75229928950"
      }
    ]
  },
  {
    "gradeName": "3.b",
    "subjects": [
      {
        "subjectName": "Matematika",
        "professorName": "Marko Marković",
        "grade": "5",
        "subjectId": "74110928740"
      }
    ]
  }
]
```

---

### Missed class percentages

**`GET /api/Scraper/CalculateMissedClassPercentages`**

Combines your timetable and absences to calculate what percentage of each
subject's yearly hours you have missed. Uses the timetable to estimate the
yearly fund of hours (weekly hours × 35 weeks ÷ 2 for semester rotation).

**Request:**
```
GET /api/Scraper/CalculateMissedClassPercentages
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "Matematika": "3.57%",
  "Fizika": "0.00%",
  "Engleski jezik": "7.14%"
}
```

---

## Error reference

Every endpoint can return these responses:

| Code | Meaning | When it happens |
|------|---------|-----------------|
| `200` | Success | Request completed, response body contains data |
| `400` | Bad request | Missing or invalid parameters |
| `401` | Unauthorized | Token missing, expired, or invalid — log in again |
| `429` | Too many requests | Either rate limit (20 req/min) or brute force lockout (5 failed logins → 15 min) |
| `500` | Server error | e-Dnevnik is down, returned unexpected HTML, or internal error |

**401 response body:**
```json
"Sesija je istekla ili token nije valjan. Potrebna je ponovna prijava."
```

**429 response body (rate limit):**
```json
{ "error": "previše zahtjeva, pokušaj ponovo za minutu." }
```

**429 response body (brute force):**
```json
"previše neuspjelih pokušaja prijave. pokušaj ponovo za 15 minuta."
```

---

## Rate limiting

The API enforces a limit of **20 requests per minute per IP address**. Exceeding
this returns `429`. The limit is shared across all endpoints — login calls count
toward the same quota.

For the login endpoint specifically, **5 consecutive failed attempts** on the same
email address triggers an additional 15-minute lockout for that email, regardless
of IP. A successful login resets the counter.

---

## Health check

**`GET /health`**

No authentication required. Returns `200` if the API is running.

```json
{ "status": "ok" }
```

Use this to verify the server is up before making authenticated calls.

---

## Complete example workflow

Below is a complete sequence for fetching a student's subjects after logging in.

### 1 — Login
```
POST /api/Login
Content-Type: application/json

{
  "email": "luka.lukic@skole.hr",
  "password": "<your-ednevnik-password>"
}
```
Response:
```json
{ "token": "a1B2c3D4...==" }
```

### 2 — Fetch subjects
```
GET /api/Scraper/ScrapeSubjectsAndProfessors
Authorization: Bearer a1B2c3D4...==
```
Response:
```json
{
  "subjects": [
    { "subjectName": "Matematika", "professorName": "Marko Marković", "grade": "4", "subjectId": "75229928950" },
    { "subjectName": "Fizika", "professorName": "Ana Anić", "grade": "5", "subjectId": "75229928951" }
  ]
}
```

### 3 — Fetch detailed grades for Matematika
```
GET /api/Scraper/ScrapeSpecificSubjectGrades?subjectId=75229928950
Authorization: Bearer a1B2c3D4...==
```

### 4 — Logout when done
```
DELETE /api/Login
Authorization: Bearer a1B2c3D4...==
```
Response: `200 OK`

---

## Notes for developers

- **Timeout:** Set your HTTP client timeout to at least **120 seconds**. The
  `ScrapeDifferentGrades` endpoint can take over a minute for students with many
  years of history.

- **Null grades:** When a subject has no average grade on the list page (common
  for subjects still in progress), the API navigates to the individual subject
  page to find the final grade. If neither exists, `grade` is returned as `"N/A"`.

- **New grades / new tests caching:** The server caches these two endpoints per
  user session for 10 minutes. They are not refreshed in the background, so the
  first call after login (or after the cache expires) is fetched live and takes ~2s.
  All calls within the next 10 minutes return the cached result instantly.

- **School year auto-selection:** After login, the API automatically activates the
  most recent school year on e-Dnevnik. This ensures that endpoints like subjects
  and absences return data even after the current school year has ended and the
  portal has no "active" class selected by default.

- **Swagger UI:** When running locally in development, Swagger UI is available at
  `http://localhost:5168/swagger` for interactive testing of all endpoints.

using System.Net;
using System.Text.Json.Nodes;
using E_Dnevnik_API.V2.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

public class V2EmailTests
{
    // the payload app.queue_consent_email writes (migration 20261115000503_parent_consent)
    private static JsonObject Consent(bool invite = true, string? first = "Ema", string expires = "2099-01-01T10:00:00+00:00") =>
        (JsonObject)JsonNode.Parse($$"""
            {
              "first_name": {{(first == null ? "null" : "\"" + first + "\"")}},
              "class_label": "2.b",
              "school": "Gimnazija Pula",
              "consent_url": "https://odlikas.hr/potvrda?t=abc123",
              "invite": {{(invite ? "{\"code\": \"OD-K7PX\", \"token\": \"tok456\"}" : "null")}},
              "shared_areas": ["grades", "matura", "streak"],
              "expires_at": "{{expires}}"
            }
            """)!;

    // --- templates ---

    [Fact]
    public void ConsentSubjectHasNoSchoolClassOrGrades()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent())!;
        Assert.Equal("Ema treba vašu potvrdu za Odlikaš", e.Subject);
        Assert.DoesNotContain("Gimnazija", e.Subject);
        Assert.DoesNotContain("2.b", e.Subject);
    }

    [Fact]
    public void ConsentBodyHasTheLinkClassSchoolAndInvite()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent())!;
        Assert.Contains("Ema (2.b · Gimnazija Pula) želi koristiti Odlikaš", e.Text);
        Assert.Contains("https://odlikas.hr/potvrda?t=abc123", e.Text);
        Assert.Contains("s vama želi dijeliti: ocjene i prosjeke, napredak i niz.", e.Text);
        Assert.Contains("https://odlikas.hr/poziv/tok456", e.Text);
        Assert.Contains("upišite kod OD-K7PX.", e.Text);
        Assert.Contains("href=\"https://odlikas.hr/potvrda?t=abc123\"", e.Html);
        Assert.Contains("POTVRĐUJEM", e.Html);
        Assert.Contains("Ne potvrđujem", e.Html);
    }

    [Fact]
    public void ConsentWithoutInviteHasNoInviteSection()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent(invite: false))!;
        Assert.DoesNotContain("Pratite napredak", e.Text);
        Assert.DoesNotContain("poziv", e.Html);
    }

    [Fact]
    public void ConsentWithoutNameIsNeutral()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent(first: null))!;
        Assert.Equal("Vaše dijete treba vašu potvrdu za Odlikaš", e.Subject);
        Assert.Contains("Budući da dijete ima manje od 16 godina", e.Text);
    }

    [Fact]
    public void HtmlIsEscaped()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent(first: "<b>Ema</b>"))!;
        Assert.DoesNotContain("<b>Ema</b>", e.Html);
        Assert.Contains("&lt;b&gt;Ema&lt;/b&gt;", e.Html);
    }

    [Fact]
    public void ConsentWithoutLinkOrUnknownKindRendersNothing()
    {
        var p = Consent();
        p.Remove("consent_url");
        Assert.Null(EmailTemplates.Render(EmailTemplates.HsConsent, p));
        Assert.Null(EmailTemplates.Render("something_else", Consent()));
    }

    [Fact]
    public void NoSerbianOrInformalWordsForParents()
    {
        var e = EmailTemplates.Render(EmailTemplates.HsConsent, Consent())!;
        foreach (var w in new[] { "odeljenje", "odjeljenje", "potvrdi ", "tvoj", " ti " })
            Assert.DoesNotContain(w, e.Text);
    }

    [Fact]
    public void ExpiredLinkIsDetected()
    {
        var now = DateTimeOffset.Parse("2026-11-15T12:00:00+00:00");
        Assert.True(EmailTemplates.IsExpired(Consent(expires: "2026-11-15T11:59:00+00:00"), now));
        Assert.False(EmailTemplates.IsExpired(Consent(), now));
        Assert.False(EmailTemplates.IsExpired(new JsonObject(), now));
    }

    [Fact]
    public void AndListMatchesTheApp()
    {
        Assert.Equal("a", EmailTemplates.AndList(new[] { "a" }));
        Assert.Equal("a i b", EmailTemplates.AndList(new[] { "a", "b" }));
        Assert.Equal("a, b i c", EmailTemplates.AndList(new[] { "a", "b", "c" }));
    }

    // --- repository ---

    [Fact]
    public void ClaimLocksSkipsAndCapsAttempts()
    {
        Assert.Contains("for update of o skip locked", EmailOutboxRepository.ClaimSql);
        Assert.Contains("o.sent_at is null", EmailOutboxRepository.ClaimSql);
        Assert.Contains("a.attempts < $3", EmailOutboxRepository.ClaimSql);
    }

    [Fact]
    public void ErrorTextCarriesTheAttemptCount()
    {
        Assert.Equal("3|resend 500: x y", EmailOutboxRepository.ErrorText(3, "resend 500: x\ny"));
        Assert.True(EmailOutboxRepository.ErrorText(1, new string('x', 2000)).Length <= EmailOutboxRepository.MaxErrorLength);
    }

    [Fact]
    public void BadPayloadIsAnEmptyObject()
    {
        Assert.Empty(EmailOutboxRepository.ParsePayload("null"));
        Assert.Empty(EmailOutboxRepository.ParsePayload("[1]"));
    }

    // --- sender ---

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, EmailSendStatus.Failed)]
    [InlineData(HttpStatusCode.BadRequest, EmailSendStatus.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, EmailSendStatus.Retry)]
    [InlineData(HttpStatusCode.Forbidden, EmailSendStatus.Retry)]
    [InlineData(HttpStatusCode.InternalServerError, EmailSendStatus.Retry)]
    public void ResendStatusMapping(HttpStatusCode code, EmailSendStatus expected)
    {
        Assert.Equal(expected, ResendEmailSender.StatusOf(code));
    }

    // --- worker ---

    private static PendingEmail Row(long id, int attempts = 0, JsonObject? payload = null) =>
        new(id, EmailTemplates.HsConsent, "roditelj@example.com", payload ?? Consent(), attempts, DateTimeOffset.UtcNow);

    [Fact]
    public async Task SentRowIsStampedWithItsIdempotencyKey()
    {
        var repo = new FakeRepo(Row(1));
        var sender = new FakeSender(EmailSendStatus.Sent);
        await Worker(repo, sender).RunOnceAsync(CancellationToken.None);
        Assert.Equal(new[] { "email_outbox/1" }, sender.Keys);
        var o = Assert.Single(repo.Outcomes);
        Assert.True(o.Sent);
    }

    [Fact]
    public async Task TransientFailureCountsAnAttemptAndBacksOff()
    {
        var repo = new FakeRepo(Row(1, attempts: 2));
        var worker = Worker(repo, new FakeSender(EmailSendStatus.Retry, "resend 500: down"));
        await worker.RunOnceAsync(CancellationToken.None);
        var o = Assert.Single(repo.Outcomes);
        Assert.False(o.Sent);
        Assert.Equal(3, o.Attempts);
        // the next round skips it until its backoff is over
        repo.Reset(Row(1, attempts: 3));
        await worker.RunOnceAsync(CancellationToken.None);
        Assert.Contains(1L, repo.LastSkip);
    }

    [Fact]
    public async Task PermanentFailureAndExpiredLinkStopRetrying()
    {
        var repo = new FakeRepo(Row(1), Row(2, payload: Consent(expires: "2000-01-01T00:00:00+00:00")));
        var sender = new FakeSender(EmailSendStatus.Failed, "resend 422: bad address");
        await Worker(repo, sender).RunOnceAsync(CancellationToken.None);
        Assert.All(repo.Outcomes, o => Assert.Equal(EmailWorker.MaxAttempts, o.Attempts));
        // the expired one is never sent
        Assert.Equal(new[] { "email_outbox/1" }, sender.Keys);
    }

    [Fact]
    public async Task RateLimitStopsTheBatch()
    {
        var repo = new FakeRepo(Row(1), Row(2));
        var sender = new FakeSender(EmailSendStatus.Retry, "resend 429: slow down");
        var n = await Worker(repo, sender).RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, n);
        Assert.Single(sender.Keys);
        Assert.Single(repo.Outcomes);
    }

    [Fact]
    public void BackoffGrows()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), EmailWorker.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(4), EmailWorker.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(64), EmailWorker.Backoff(4));
    }

    private static EmailWorker Worker(FakeRepo repo, FakeSender sender) =>
        new(repo, sender, NullLogger<EmailWorker>.Instance, TimeProvider.System);

    private sealed class FakeRepo : IEmailOutboxRepository, IEmailClaim
    {
        private List<PendingEmail> _rows;

        public FakeRepo(params PendingEmail[] rows) => _rows = rows.ToList();

        public List<EmailOutcome> Outcomes { get; } = new();
        public List<long> LastSkip { get; private set; } = new();
        public bool IsConfigured => true;
        public IReadOnlyList<PendingEmail> Rows => _rows;

        public void Reset(params PendingEmail[] rows)
        {
            _rows = rows.ToList();
            Outcomes.Clear();
        }

        public Task<IEmailClaim> ClaimAsync(IReadOnlyCollection<string> kinds, int limit, int maxAttempts,
            IReadOnlyCollection<long> skip, CancellationToken ct)
        {
            LastSkip = skip.ToList();
            return Task.FromResult<IEmailClaim>(this);
        }

        public Task CompleteAsync(IReadOnlyCollection<EmailOutcome> outcomes, CancellationToken ct)
        {
            Outcomes.AddRange(outcomes);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSender : IEmailSender
    {
        private readonly EmailSendStatus _status;
        private readonly string? _error;

        public FakeSender(EmailSendStatus status, string? error = null)
        {
            _status = status;
            _error = error;
        }

        public List<string> Keys { get; } = new();
        public bool IsConfigured => true;

        public Task<EmailSendResult> SendAsync(string to, RenderedEmail email, string idempotencyKey, CancellationToken ct)
        {
            Keys.Add(idempotencyKey);
            return Task.FromResult(new EmailSendResult(_status, _error));
        }
    }
}

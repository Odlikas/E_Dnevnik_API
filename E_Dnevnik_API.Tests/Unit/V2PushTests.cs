using E_Dnevnik_API.V2.Push;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

public class V2PushTests
{
    private static readonly Guid Family = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherFamily = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Child = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Mama = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Tata = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Phone = Guid.Parse("dddddddd-0000-0000-0000-000000000001");

    private static PendingPush Grade(
        string subject = "Matematika",
        short value = 5,
        string? note = "Odličan odgovor",
        string? element = "Usmeno",
        Guid? parent = null,
        string type = PushPlanner.NewGrade
    ) => new(Guid.NewGuid(), Family, parent, Child, type, "Ema", subject, value, note, element);

    private static readonly PushRecipient MamaR = new(RecipientKind.Parent, Mama, Family, null, "mama-fid");
    private static readonly PushRecipient TataR = new(RecipientKind.Parent, Tata, Family, null, "tata-fid");
    private static readonly PushRecipient KidR = new(RecipientKind.Device, Phone, Family, Child, "kid-fid");
    private static readonly PushRecipient Stranger = new(RecipientKind.Parent, Guid.NewGuid(), OtherFamily, null, "x-fid");
    private static readonly PushRecipient[] All = { MamaR, TataR, KidR, Stranger };

    // --- recipients ---

    [Fact]
    public void FamilyWideRowGoesToEveryParentAndTheKidPhone()
    {
        var who = PushPlanner.RecipientsOf(Grade(), All).Select(r => r.Token).ToList();
        Assert.Equal(new[] { "mama-fid", "tata-fid", "kid-fid" }, who);
    }

    [Fact]
    public void RowForOneParentGoesOnlyToThatParent()
    {
        var who = PushPlanner.RecipientsOf(Grade(parent: Tata), All).Select(r => r.Token).ToList();
        Assert.Equal(new[] { "tata-fid" }, who);
    }

    [Fact]
    public void UnknownTypesAreNotPlanned()
    {
        Assert.Empty(PushPlanner.Plan(new[] { Grade(type: "tamper") }, All, details: true));
    }

    [Fact]
    public void HelpRequestGoesOnlyToParentsWithoutNamesByDefault()
    {
        var help = Grade(type: PushPlanner.HelpRequest, note: null, element: null);
        var plan = PushPlanner.Plan(new[] { help }, All, details: false);
        Assert.Equal(new[] { "mama-fid", "tata-fid" }, plan.Select(p => p.Token));
        Assert.All(plan, p => Assert.Equal("Dijete treba pomoć", p.Title));
        Assert.Equal("help_request", plan[0].Data["type"]);
        var detailed = PushPlanner.Plan(new[] { help }, All, details: true);
        Assert.Equal("Ema treba pomoć", detailed[0].Title);
    }

    [Fact]
    public void GradesAndHelpRequestsAreSeparatePushes()
    {
        var plan = PushPlanner.Plan(new[] { Grade(), Grade(type: PushPlanner.HelpRequest) }, new[] { MamaR }, details: false);
        Assert.Equal(2, plan.Count);
        Assert.Equal(new[] { "Nova ocjena", "Dijete treba pomoć" }, plan.Select(p => p.Title));
    }

    // --- text ---

    [Fact]
    public void DetailedKidText()
    {
        var (title, body) = PushPlanner.Text(new[] { Grade() }, RecipientKind.Device, details: true);
        Assert.Equal("Nova ocjena: Matematika 5", title);
        Assert.Equal("Odličan odgovor", body);
    }

    [Fact]
    public void DetailedParentTextHasTheChildsName()
    {
        var (title, _) = PushPlanner.Text(new[] { Grade() }, RecipientKind.Parent, details: true);
        Assert.Equal("Ema: nova ocjena – Matematika 5", title);
    }

    [Fact]
    public void BodyFallsBackToTheGradingElementWithoutSchoolCode()
    {
        var g = Grade(note: "  ", element: "0012  Motorička   postignuća");
        Assert.Equal("Motorička postignuća", PushPlanner.BodyOf(g));
    }

    [Fact]
    public void PrivateTextHasNoGradeSubjectOrName()
    {
        var (title, body) = PushPlanner.Text(new[] { Grade() }, RecipientKind.Parent, details: false);
        Assert.Equal("Nova ocjena", title);
        Assert.DoesNotContain("Ema", title + body);
        Assert.DoesNotContain("Matematika", title + body);
        Assert.DoesNotContain("5", title + body);
    }

    [Fact]
    public void SeveralGradesForOnePhoneBecomeOnePush()
    {
        var rows = new[] { Grade(), Grade("Fizika", 4), Grade("Kemija", 3) };
        var pushes = PushPlanner.Plan(rows, All, details: true);
        Assert.Equal(3, pushes.Count); // mama, tata, kid
        var kid = pushes.Single(p => p.Token == "kid-fid");
        Assert.Equal("3 nove ocjene", kid.Title);
        Assert.Equal("Matematika 5, Fizika 4, Kemija 3", kid.Body);
        Assert.Equal(3, kid.Notifications.Count);
        Assert.Equal("Ema: 3 nove ocjene", pushes.Single(p => p.Token == "mama-fid").Title);
        Assert.Equal("3", kid.Data["count"]);
        Assert.Equal(Child.ToString(), kid.Data["child_id"]);
    }

    [Theory]
    [InlineData(1, "1 nova ocjena")]
    [InlineData(2, "2 nove ocjene")]
    [InlineData(4, "4 nove ocjene")]
    [InlineData(5, "5 novih ocjena")]
    [InlineData(11, "11 novih ocjena")]
    [InlineData(12, "12 novih ocjena")]
    [InlineData(21, "21 nova ocjena")]
    [InlineData(22, "22 nove ocjene")]
    public void CroatianPlural(int n, string text) => Assert.Equal(text, PushPlanner.CountTitle(n));

    // --- SQL shape (the same queries are checked by supabase/tests/17_push_sender.sql) ---

    [Fact]
    public void ClaimSqlLocksOnlyUnsentRowsOfTheLastDay()
    {
        var sql = PushRepository.ClaimSql;
        Assert.Contains("n.pushed_at is null", sql);
        Assert.Contains("n.created_at > now() - interval '24 hours'", sql);
        Assert.Contains("n.type = any($1)", sql);
        Assert.Contains("limit $2", sql);
        Assert.EndsWith("for update of n skip locked", sql);
        // nothing the service has no grant for (read_at, birth_year, display_name)
        Assert.DoesNotContain("read_at", sql);
        Assert.DoesNotContain("display_name", sql);
    }

    [Fact]
    public void RecipientsSqlUsesOnlyTheCurrentPhone()
    {
        Assert.Contains("d.revoked_at is null", PushRepository.RecipientsSql);
        Assert.Contains("p.family_id = any($1)", PushRepository.RecipientsSql);
        Assert.Contains("d.child_id = any($2)", PushRepository.RecipientsSql);
    }

    [Fact]
    public void ClearingOnlyTouchesTheRejectedToken()
    {
        Assert.Contains("where push_token = any($1)", PushRepository.ClearParentTokensSql);
        Assert.Contains("where push_token = any($1)", PushRepository.ClearDeviceTokensSql);
    }

    // --- FCM ---

    [Fact]
    public void MessageCarriesTokenTextAndData()
    {
        var p = PushPlanner.Plan(new[] { Grade() }, new[] { KidR }, details: false).Single();
        var m = FcmPushSender.ToMessage(p);
        Assert.Equal("kid-fid", m.Token);
        Assert.Equal("Nova ocjena", m.Notification.Title);
        Assert.Equal(PushPlanner.NewGrade, m.Data["type"]);
    }

    [Fact]
    public void SenderIsOffWithoutCredentials()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.False(new FcmPushSender(config, NullLogger<FcmPushSender>.Instance).IsConfigured);
    }

    // --- worker round ---

    private sealed class FakeClaim : IPushClaim
    {
        public IReadOnlyList<PendingPush> Rows { get; init; } = Array.Empty<PendingPush>();
        public IReadOnlyList<PushRecipient> Recipients { get; init; } = Array.Empty<PushRecipient>();
        public List<Guid>? Pushed;
        public List<string>? Dead;
        public bool Disposed;

        public Task CompleteAsync(IReadOnlyCollection<Guid> pushed, IReadOnlyCollection<string> deadTokens, CancellationToken ct)
        {
            Pushed = pushed.ToList();
            Dead = deadTokens.ToList();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeRepo : IPushRepository
    {
        public FakeClaim Claim = new();
        public bool IsConfigured => true;

        public Task<IPushClaim> ClaimAsync(IReadOnlyCollection<string> types, int limit, CancellationToken ct) =>
            Task.FromResult<IPushClaim>(Claim);
    }

    private sealed class FakeSender : IPushSender
    {
        public Func<OutgoingPush, SendStatus> Status = _ => SendStatus.Sent;
        public bool Throw;
        public List<OutgoingPush> Sent = new();
        public bool IsConfigured => true;

        public Task<IReadOnlyList<SendResult>> SendAsync(IReadOnlyList<OutgoingPush> pushes, CancellationToken ct)
        {
            if (Throw)
                throw new HttpRequestException("offline");
            Sent.AddRange(pushes);
            return Task.FromResult<IReadOnlyList<SendResult>>(pushes.Select(p => new SendResult(p.Token, Status(p))).ToList());
        }
    }

    private static PushWorker Worker(FakeRepo repo, FakeSender sender) =>
        new(repo, sender, new ConfigurationBuilder().AddInMemoryCollection().Build(), NullLogger<PushWorker>.Instance);

    [Fact]
    public async Task RoundSendsStampsAndClearsDeadTokens()
    {
        var row = Grade();
        var repo = new FakeRepo { Claim = new FakeClaim { Rows = new[] { row }, Recipients = new[] { MamaR, KidR } } };
        var sender = new FakeSender { Status = p => p.Token == "kid-fid" ? SendStatus.DeadToken : SendStatus.Sent };

        Assert.Equal(1, await Worker(repo, sender).RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal(new[] { row.Id }, repo.Claim.Pushed);
        Assert.Equal(new[] { "kid-fid" }, repo.Claim.Dead);
        Assert.True(repo.Claim.Disposed);
    }

    [Fact]
    public async Task RowWithoutRecipientsIsStillStamped()
    {
        var row = Grade();
        var repo = new FakeRepo { Claim = new FakeClaim { Rows = new[] { row } } };
        var sender = new FakeSender();
        await Worker(repo, sender).RunOnceAsync(CancellationToken.None);
        Assert.Empty(sender.Sent);
        Assert.Equal(new[] { row.Id }, repo.Claim.Pushed);
    }

    [Fact]
    public async Task FcmFailureLeavesRowsForTheNextRound()
    {
        var repo = new FakeRepo { Claim = new FakeClaim { Rows = new[] { Grade() }, Recipients = new[] { MamaR } } };
        var sender = new FakeSender { Throw = true };
        await Assert.ThrowsAsync<HttpRequestException>(() => Worker(repo, sender).RunOnceAsync(CancellationToken.None));
        Assert.Null(repo.Claim.Pushed); // not completed: rolled back on dispose
        Assert.True(repo.Claim.Disposed);
    }
}

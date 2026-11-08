using E_Dnevnik_API.Controllers;
using E_Dnevnik_API.V2.Identity;
using E_Dnevnik_API.V2.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

public class V2IdentityTests
{
    private const string Salt = "test-salt-0123456789";
    private static readonly Guid Caller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly SyncCookie[] Cookies = { new() { Name = "PHPSESSID", Value = "x" } };

    // izmišljen učenik; oblik kao /personal_data (ime, razred, škola, redovi l-two-columns)
    private static string ProfileHtml(
        string name = "Ivan Marić",
        string cls = "1.b",
        string school = "Gimnazija Pula",
        string birth = "14.03.2011.",
        string? username = null
    ) =>
        $"""
        <html><body>
        <div id="page-wrapper">
          <div class="logged-in-user"><div class="user-name"><span>{name}</span></div></div>
          <div class="school-data">
            <div class="class"><span class="bold">{cls}</span><span class="class-schoolyear">2026./2027.</span></div>
            <div class="school">
              <span class="school-name">{school}</span><span class="school-city">Pula</span>
              <div class="classmaster"><span>Razrednik</span><span>Ana Anić</span></div>
            </div>
          </div>
          <div class="l-two-columns"><span class="label">Ime i prezime</span><span class="column">{name}</span></div>
          <div class="l-two-columns"><span class="label">Datum rođenja</span><span class="column">{birth}</span></div>
          <div class="l-two-columns"><span class="label">Mjesto rođenja</span><span class="column">Pula</span></div>
          {(username is null ? "" : $"<div class=\"l-two-columns\"><span class=\"label\">Korisnička oznaka</span><span class=\"column\">{username}</span></div>")}
          <div class="l-two-columns"></div>
          <div class="l-two-columns"></div>
          <div class="l-two-columns"></div>
          <div class="l-two-columns"></div>
          {(username is null ? "<div class=\"l-two-columns\"></div>" : "")}
          <div class="l-two-columns"><span class="column">opća gimnazija</span></div>
        </div>
        </body></html>
        """;

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeScraper : IEdnevnikIdentityScraper
    {
        public string Html = ProfileHtml();
        public Exception? Throw;

        public Task<string> FetchProfileHtmlAsync(IReadOnlyList<SyncCookie> cookies, CancellationToken ct) =>
            Throw is null ? Task.FromResult(Html) : Task.FromException<string>(Throw);
    }

    private sealed class FakeRepo : IIdentityRepository
    {
        public IdentityBind? Last;
        public string? Error;

        public bool IsConfigured => true;

        public Task<IdentityBindResult> BindAsync(IdentityBind bind, CancellationToken ct)
        {
            Last = bind;
            if (Error != null)
                throw new IdentityBindException(Error);
            return Task.FromResult(new IdentityBindResult(Child, Guid.NewGuid(), true, bind.NeedsParentConsent));
        }
    }

    private sealed class FakeSync : ISyncService
    {
        public SyncOutcome Outcome = new(SyncOutcomeKind.Ok, new SyncResponse { SubjectCount = 14, School = "Gimnazija Pula" });
        public Guid? SyncedChild;

        public Task<SyncOutcome> SyncAsync(Guid callerId, Guid childId, IReadOnlyList<SyncCookie> cookies, CancellationToken ct)
        {
            SyncedChild = childId;
            return Task.FromResult(Outcome);
        }
    }

    private static (IdentityService, FakeScraper, FakeRepo, FakeSync) Service(string? salt = Salt)
    {
        var scraper = new FakeScraper();
        var repo = new FakeRepo();
        var sync = new FakeSync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [IdentityService.SaltKey] = salt })
            .Build();
        var service = new IdentityService(
            scraper,
            repo,
            sync,
            new FixedTime(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero)),
            config,
            NullLogger<IdentityService>.Instance
        );
        return (service, scraper, repo, sync);
    }

    // --- profile ---

    [Fact]
    public void ParseProfile_ReadsNameClassSchoolProgramAndBirthDate()
    {
        var p = IdentityRules.ParseProfile(ProfileHtml())!;
        Assert.Equal("Ivan Marić", p.FullName);
        Assert.Equal("Ivan", p.FirstName);
        Assert.Equal("1.b", p.ClassRaw);
        Assert.Equal("Gimnazija Pula", p.School);
        Assert.Equal("opća gimnazija", p.Program);
        Assert.Equal(new DateOnly(2011, 3, 14), p.BirthDate);
        Assert.Null(p.Username);
    }

    [Fact]
    public void ParseProfile_TakesTheAaiUsernameWhenShown()
    {
        var p = IdentityRules.ParseProfile(ProfileHtml(username: "Ivan.Maric2@skole.hr"))!;
        Assert.Equal("ivan.maric2@skole.hr", p.Username);
        Assert.Equal("opća gimnazija", p.Program);
    }

    [Theory]
    [InlineData("3. 7. 2009.", 2009, 7, 3)]
    [InlineData("31.12.2010", 2010, 12, 31)]
    public void ParseProfile_BirthDateFormats(string text, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), IdentityRules.ParseProfile(ProfileHtml(birth: text))!.BirthDate);
    }

    [Fact]
    public void BirthShape_MasksPersonalData()
    {
        var shape = IdentityRules.BirthShape(ProfileHtml(birth: "14.03.2011."));
        Assert.Contains("Datum rođenja", shape);
        Assert.Contains("99.99.9999", shape);
        Assert.DoesNotContain("2011", shape);
        Assert.DoesNotContain("Pula", shape);
    }

    [Fact]
    public void ParseProfile_NullWithoutName_BirthDateNullWhenMissing()
    {
        Assert.Null(IdentityRules.ParseProfile("<html><body><p>nešto drugo</p></body></html>"));
        Assert.Null(IdentityRules.ParseProfile(ProfileHtml(birth: "nepoznato"))!.BirthDate);
    }

    // --- rules ---

    [Fact]
    public void Hash_IsSha256OfSaltAndLowerUsername()
    {
        var h = IdentityRules.Hash("s", " Ivan.Maric@SKOLE.hr ");
        Assert.Equal(IdentityRules.Hash("s", "ivan.maric@skole.hr"), h);
        Assert.Matches("^[0-9a-f]{64}$", h);
        // sha256("abc") poznata vrijednost: sol "a" + ime "bc"
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", IdentityRules.Hash("a", "BC"));
        Assert.NotEqual(h, IdentityRules.Hash("t", "ivan.maric@skole.hr"));
    }

    [Theory]
    [InlineData(2011, 3, 14, true)] // 15
    [InlineData(2010, 11, 8, false)] // 16 danas
    [InlineData(2010, 11, 9, true)] // 16 sutra
    [InlineData(2008, 1, 1, false)]
    public void NeedsParentConsent_Under16(int y, int m, int d, bool expected)
    {
        Assert.Equal(expected, IdentityRules.NeedsParentConsent(new DateOnly(y, m, d), new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void NeedsParentConsent_UnknownBirthDateAsksForConsent()
    {
        Assert.True(IdentityRules.NeedsParentConsent(null, new DateOnly(2026, 11, 8)));
    }

    [Theory]
    [InlineData("4.E-4", "Gimnazija Pula", (short)4)]
    [InlineData("1.b", "Tehnička škola", (short)1)]
    [InlineData("7.b", "Gimnazija Pula", null)]
    [InlineData("3.a", "OŠ Vladimira Nazora", null)]
    [InlineData("3.a", "Osnovna škola Veli Vrh", null)]
    [InlineData(null, "Gimnazija Pula", null)]
    public void HighSchoolGrade(string? cls, string school, short? expected)
    {
        Assert.Equal(expected, IdentityRules.HighSchoolGrade(cls, school));
    }

    [Theory]
    [InlineData("ivan.maric@skole.hr", "Ivan Marić", true)]
    [InlineData("ivan.maric2@skole.hr", "Ivan Marić", true)]
    [InlineData("djurdja.horvat@skole.hr", "Đurđa Horvat", true)]
    [InlineData("durda.horvat@skole.hr", "Đurđa Horvat", true)]
    [InlineData("ana.maria.kovac@skole.hr", "Ana-Maria Kovač", true)]
    [InlineData("petra.maric@skole.hr", "Ivan Marić", false)]
    [InlineData("@skole.hr", "Ivan Marić", false)]
    public void UsernameMatchesName(string username, string name, bool expected)
    {
        Assert.Equal(expected, IdentityRules.UsernameMatchesName(username, name));
    }

    // --- service ---

    [Fact]
    public async Task Identify_BindsHashesSyncsAndReturnsTheAccount()
    {
        var (service, _, repo, sync) = Service();
        var outcome = await service.IdentifyAsync(Caller, "Ivan.Maric@skole.hr", Cookies, default);

        Assert.Equal(IdentityOutcomeKind.Ok, outcome.Kind);
        Assert.Equal(Caller, repo.Last!.UserId);
        Assert.Equal(IdentityRules.Hash(Salt, "ivan.maric@skole.hr"), repo.Last.Hash);
        Assert.Equal((short)1, repo.Last.Grade);
        Assert.Equal((short)2011, repo.Last.BirthYear);
        Assert.True(repo.Last.NeedsParentConsent);
        Assert.Equal(Child, sync.SyncedChild);

        var r = outcome.Response!;
        Assert.Equal(Child, r.ChildId);
        Assert.Equal("Ivan", r.FirstName);
        Assert.Equal("Ivan Marić", r.FullName);
        Assert.Equal("1.b", r.ClassName);
        Assert.Equal("opća gimnazija", r.Program);
        Assert.Equal(14, r.SubjectCount);
        Assert.Equal("2011-03-14", r.BirthDate);
        Assert.True(r.NeedsParentConsent);
        Assert.True(r.Synced);
    }

    [Fact]
    public async Task Identify_ProfileUsernameWinsOverTheRequest()
    {
        var (service, scraper, repo, _) = Service();
        scraper.Html = ProfileHtml(username: "ivan.maric7@skole.hr");
        var outcome = await service.IdentifyAsync(Caller, "petra.peric@skole.hr", Cookies, default);
        Assert.Equal(IdentityOutcomeKind.Ok, outcome.Kind);
        Assert.Equal(IdentityRules.Hash(Salt, "ivan.maric7@skole.hr"), repo.Last!.Hash);
    }

    [Fact]
    public async Task Identify_RefusesSomeoneElsesUsername()
    {
        var (service, _, repo, _) = Service();
        var outcome = await service.IdentifyAsync(Caller, "petra.peric@skole.hr", Cookies, default);
        Assert.Equal(IdentityOutcomeKind.Mismatch, outcome.Kind);
        Assert.Null(repo.Last);
    }

    [Fact]
    public async Task Identify_NeedsAUsernameWhenTheProfileHasNone()
    {
        var (service, _, _, _) = Service();
        Assert.Equal(IdentityOutcomeKind.BadRequest, (await service.IdentifyAsync(Caller, " ", Cookies, default)).Kind);
    }

    [Fact]
    public async Task Identify_PrimarySchoolIsRefused()
    {
        var (service, scraper, repo, _) = Service();
        scraper.Html = ProfileHtml(cls: "7.b", school: "OŠ Vladimira Nazora");
        Assert.Equal(
            IdentityOutcomeKind.NotHighSchool,
            (await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default)).Kind
        );
        Assert.Null(repo.Last);
    }

    [Fact]
    public async Task Identify_MapsScraperAndBindErrors()
    {
        var (service, scraper, repo, _) = Service();
        scraper.Throw = new EdnevnikSessionInvalidException();
        Assert.Equal(IdentityOutcomeKind.SessionInvalid, (await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default)).Kind);
        scraper.Throw = new EdnevnikUnavailableException("down");
        Assert.Equal(IdentityOutcomeKind.Unavailable, (await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default)).Kind);
        scraper.Throw = null;
        repo.Error = "already_bound";
        Assert.Equal(IdentityOutcomeKind.AlreadyBound, (await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default)).Kind);
        repo.Error = "not_a_student_user";
        Assert.Equal(IdentityOutcomeKind.AlreadyBound, (await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default)).Kind);
    }

    [Fact]
    public async Task Identify_AccountStillReturnedWhenTheFirstSyncFails()
    {
        var (service, _, _, sync) = Service();
        sync.Outcome = new SyncOutcome(SyncOutcomeKind.Unavailable);
        var outcome = await service.IdentifyAsync(Caller, "ivan.maric@skole.hr", Cookies, default);
        Assert.Equal(IdentityOutcomeKind.Ok, outcome.Kind);
        Assert.False(outcome.Response!.Synced);
        Assert.Equal(0, outcome.Response.SubjectCount);
        Assert.Equal("Gimnazija Pula", outcome.Response.School);
    }

    [Fact]
    public void Service_NotConfiguredWithoutALongEnoughSalt()
    {
        Assert.True(Service().Item1.IsConfigured);
        Assert.False(Service(null).Item1.IsConfigured);
        Assert.False(Service("short").Item1.IsConfigured);
    }

    [Fact]
    public void Request_Validation()
    {
        Assert.True(V2IdentityController.IsValid(new IdentityRequest { Username = "a@skole.hr", Cookies = Cookies.ToList() }));
        Assert.True(V2IdentityController.IsValid(new IdentityRequest { Cookies = Cookies.ToList() }));
        Assert.False(V2IdentityController.IsValid(new IdentityRequest { Username = "a" }));
        Assert.False(V2IdentityController.IsValid(new IdentityRequest { Cookies = new List<SyncCookie>() }));
        Assert.False(V2IdentityController.IsValid(new IdentityRequest { Username = new string('a', 300), Cookies = Cookies.ToList() }));
        Assert.False(V2IdentityController.IsValid(null));
    }
}

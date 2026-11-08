using System.Net;
using E_Dnevnik_API.Models.ScrapeStudentProfile;
using E_Dnevnik_API.Models.ScrapeSubjects;
using E_Dnevnik_API.Models.ScrapeTests;
using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.V2.Sync;
using RichardSzalay.MockHttp;

namespace E_Dnevnik_API.Tests.Unit;

public class V2SyncTests
{
    private static readonly Guid Child = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly SubjectRow[] Seed =
    {
        new(1, "MAT", "Matematika"),
        new(2, "HRV", "Hrvatski jezik"),
        new(3, "ENG", "Engleski jezik"),
        new(4, "PID", "Priroda i društvo"),
        new(8, "FIZ", "Fizika"),
        new(12, "NJE", "Njemački jezik"),
    };

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // --- date -> year ---

    [Theory]
    [InlineData(2026, 11, 7, 2026)]
    [InlineData(2026, 8, 1, 2026)]
    [InlineData(2027, 7, 31, 2026)]
    [InlineData(2027, 1, 15, 2026)]
    public void SchoolYear_Containing(int y, int m, int d, int start)
    {
        Assert.Equal(start, SchoolYear.Containing(new DateOnly(y, m, d)).StartYear);
    }

    [Theory]
    [InlineData("10.12.", "2026-12-10")]
    [InlineData("1.9.", "2026-09-01")]
    [InlineData("15. 1.", "2027-01-15")]
    [InlineData("30.06.", "2027-06-30")]
    [InlineData("31.08.", "2026-08-31")]
    [InlineData("03.02.2027.", "2027-02-03")]
    public void SchoolYear_ParseDate_DerivesYear(string text, string expected)
    {
        var year = new SchoolYear(2026);
        Assert.Equal(DateOnly.Parse(expected), year.ParseDate(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    [InlineData("32.12.")]
    [InlineData("10.13.")]
    [InlineData("29.02.")] // 2027 nije prijestupna
    public void SchoolYear_ParseDate_RejectsBadDates(string text)
    {
        Assert.Null(new SchoolYear(2026).ParseDate(text));
    }

    [Fact]
    public void SchoolYear_LabelAndRange()
    {
        var y = new SchoolYear(2026);
        Assert.Equal("2026/2027", y.Label);
        Assert.Equal(new DateOnly(2026, 8, 1), y.FirstDay);
        Assert.Equal(new DateOnly(2027, 7, 31), y.LastDay);
    }

    [Fact]
    public void SchoolYear_UsesZagrebDate()
    {
        // 31.7. 23:30 UTC = 1.8. 01:30 u Zagrebu -> nova školska godina
        var time = new FixedTime(new DateTimeOffset(2026, 7, 31, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 8, 1), SchoolYear.ZagrebToday(time));
        Assert.Equal(2026, SchoolYear.Current(time).StartYear);
    }

    // --- grade values ---

    [Theory]
    [InlineData("5", (short)5)]
    [InlineData(" 1 ", (short)1)]
    [InlineData("4 (vrlo dobar)", null)]
    [InlineData("0", null)]
    [InlineData("6", null)]
    [InlineData("N/A", null)]
    [InlineData("+", null)]
    [InlineData("12", null)]
    [InlineData("", null)]
    public void ParseValue_OnlyOneToFive(string text, short? expected)
    {
        Assert.Equal(expected, GradeRows.ParseValue(text));
    }

    // --- source_hash ---

    [Fact]
    public void Hash_IsSha256HexOfPipeJoinedFields()
    {
        var rows = GradeRows.WithHashes(
            Child,
            1,
            new[] { new ParsedGrade(new DateOnly(2026, 12, 10), 5, "Usmeno", "Derivacije") }
        );
        var expected = GradeRows.Hash($"{Child:D}|1|2026-12-10|5|Usmeno|Derivacije|0");
        Assert.Equal(expected, rows[0].SourceHash);
        Assert.Matches("^[0-9a-f]{64}$", rows[0].SourceHash);
        // poznata vrijednost sha256("abc")
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", GradeRows.Hash("abc"));
    }

    [Fact]
    public void Hash_DuplicateCounter_KeepsTwoIdenticalGradesAsTwoRows()
    {
        var g = new ParsedGrade(new DateOnly(2026, 12, 10), 5, "ocjena", null);
        var other = new ParsedGrade(new DateOnly(2026, 12, 10), 4, "ocjena", null);
        var rows = GradeRows.WithHashes(Child, 1, new[] { g, other, g });

        Assert.Equal(3, rows.Count);
        Assert.Equal(GradeRows.Hash($"{Child:D}|1|2026-12-10|5|ocjena||0"), rows[0].SourceHash);
        Assert.Equal(GradeRows.Hash($"{Child:D}|1|2026-12-10|4|ocjena||0"), rows[1].SourceHash);
        Assert.Equal(GradeRows.Hash($"{Child:D}|1|2026-12-10|5|ocjena||1"), rows[2].SourceHash);
        Assert.Equal(3, rows.Select(r => r.SourceHash).Distinct().Count());
    }

    [Fact]
    public void Hash_IsStableAcrossSyncs()
    {
        var g = new[] { new ParsedGrade(new DateOnly(2027, 1, 5), 3, "Pisano", null) };
        Assert.Equal(GradeRows.WithHashes(Child, 8, g)[0].SourceHash, GradeRows.WithHashes(Child, 8, g)[0].SourceHash);
        Assert.NotEqual(
            GradeRows.WithHashes(Child, 8, g)[0].SourceHash,
            GradeRows.WithHashes(Child, 1, g)[0].SourceHash
        );
    }

    // --- subject mapping ---

    [Theory]
    [InlineData("Matematika", (short)1)]
    [InlineData("  MATEMATIKA ", (short)1)]
    [InlineData("Hrvatski jezik", (short)2)]
    [InlineData("Hrvatski", (short)2)]
    [InlineData("Engleski jezik (prvi strani jezik)", (short)3)]
    [InlineData("Engleski jezik - izborni", (short)3)]
    [InlineData("Engleski jezik, drugi strani jezik", (short)3)]
    [InlineData("Njemacki jezik", (short)12)]
    [InlineData("Njemački jezik - izborna nastava", (short)12)]
    [InlineData("Priroda i drustvo", (short)4)]
    public void SubjectMapper_MapsVariants(string name, short id)
    {
        Assert.Equal(id, new SubjectMapper(Seed).Map(name)?.Id);
    }

    [Theory]
    [InlineData("Glazbena kultura")]
    [InlineData("Vjeronauk")]
    [InlineData("")]
    [InlineData("N/A")]
    public void SubjectMapper_UnknownIsNull(string name)
    {
        Assert.Null(new SubjectMapper(Seed).Map(name));
    }

    [Fact]
    public void SubjectMapper_Normalize()
    {
        Assert.Equal("njemacki jezik", SubjectMapper.Normalize("  Njemački   JEZIK. "));
        Assert.Equal("priroda i drustvo", SubjectMapper.Normalize("Priroda i društvo"));
    }

    [Theory]
    [InlineData("Engleski jezik I", "engleski jezik")]
    [InlineData("Matematika IV", "matematika")]
    [InlineData("Njemački jezik VIII", "njemacki jezik")]
    [InlineData("Fizika", "fizika")]
    [InlineData("Hrvatski jezik i književnost", "hrvatski jezik i knjizevnost")]
    public void SubjectMapper_Normalize_DropsTrailingRoman(string name, string expected)
    {
        Assert.Equal(expected, SubjectMapper.Normalize(name));
    }

    [Theory]
    [InlineData("Engleski jezik I", (short)3)]
    [InlineData("Matematika II", (short)1)]
    public void SubjectMapper_MapsRomanNumeralNames(string name, short id)
    {
        Assert.Equal(id, new SubjectMapper(Seed).Map(name)?.Id);
    }

    [Theory]
    [InlineData("Sat razrednika", true)]
    [InlineData("  sat   razrednika ", true)]
    [InlineData("Matematika", false)]
    [InlineData("", false)]
    public void SubjectMapper_IsHomeroom(string name, bool expected)
    {
        Assert.Equal(expected, SubjectMapper.IsHomeroom(name));
    }

    // --- dead session ---

    private const string LoginForm = """
        <html><body><form method="post" action="/login">
          <input type="hidden" name="csrf_token" value="abc">
          <input type="text" name="username"><input type="password" name="password">
        </form></body></html>
        """;

    [Theory]
    [InlineData("https://ocjene.skole.hr/login")]
    [InlineData("https://ocjene.skole.hr/login/")]
    [InlineData("https://ocjene.skole.hr/login?next=%2Fcourse")]
    public void Detector_LoginUrlIsDead(string url)
    {
        Assert.True(EdnevnikSessionDetector.IsLoginPage(new Uri(url), "<html></html>"));
    }

    [Fact]
    public void Detector_LoginFormIsDead()
    {
        Assert.True(EdnevnikSessionDetector.IsLoginPage(new Uri("https://ocjene.skole.hr/course"), LoginForm));
    }

    [Fact]
    public void Detector_NormalPageIsAlive()
    {
        // csrf_token u drugoj formi (npr. odabir razreda) nije prijava
        const string page = """
            <html><body><ul class="list"></ul>
            <form method="post"><input type="hidden" name="csrf_token" value="x"><button>Odjava</button></form>
            </body></html>
            """;
        Assert.False(EdnevnikSessionDetector.IsLoginPage(new Uri("https://ocjene.skole.hr/course"), page));
        Assert.False(EdnevnikSessionDetector.IsLoginPage(new Uri("https://ocjene.skole.hr/loginx"), null));
        Assert.False(EdnevnikSessionDetector.IsLoginPage(null, null));
    }

    private sealed class RedirectedToLogin : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            // kao HttpClientHandler nakon praćenja 302: RequestMessage pokazuje konačni url
            var final = new HttpRequestMessage(HttpMethod.Get, "https://ocjene.skole.hr/login");
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = final,
                    Content = new StringContent(LoginForm),
                }
            );
        }
    }

    [Fact]
    public async Task SessionHandler_ThrowsWhenRedirectedToLogin()
    {
        using var client = new HttpClient(
            new EdnevnikSessionHandler(TimeProvider.System, TimeSpan.Zero) { InnerHandler = new RedirectedToLogin() }
        );
        await Assert.ThrowsAsync<EdnevnikSessionInvalidException>(() => new ScraperService().ScrapeSubjects(client));
    }

    [Fact]
    public async Task SessionHandler_PassesNormalPagesAndKeepsBodyReadable()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("https://ocjene.skole.hr/course")
            .Respond(
                "text/html",
                """<ul class="list"><li><a href="/grade/1"><div class="course-info"><span>Fizika</span><span>Prof</span></div></a></li></ul>"""
            );
        using var client = new HttpClient(
            new EdnevnikSessionHandler(TimeProvider.System, TimeSpan.Zero) { InnerHandler = mock }
        );
        var result = await new ScraperService().ScrapeSubjects(client);
        Assert.Equal("Fizika", result.Subjects![0].SubjectName);
    }

    [Fact]
    public async Task SessionHandler_KeepsGapBetweenRequests()
    {
        var mock = new MockHttpMessageHandler();
        mock.When("*").Respond("text/html", "<html></html>");
        using var client = new HttpClient(
            new EdnevnikSessionHandler(TimeProvider.System, TimeSpan.FromMilliseconds(200)) { InnerHandler = mock }
        );
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.GetAsync("https://ocjene.skole.hr/a");
        await client.GetAsync("https://ocjene.skole.hr/b");
        await client.GetAsync("https://ocjene.skole.hr/c");
        Assert.True(sw.ElapsedMilliseconds >= 380, $"elapsed {sw.ElapsedMilliseconds} ms");
    }

    // --- summary ---

    [Fact]
    public void Weakest_LowestAverageBelowThreshold()
    {
        var (name, avg) = SyncSummary.Weakest(
            new (string, IReadOnlyCollection<short>)[]
            {
                ("Matematika", new short[] { 2, 3, 3 }),
                ("Fizika", new short[] { 2, 2, 3 }),
                ("Hrvatski jezik", new short[] { 5 }),
                ("Engleski jezik", Array.Empty<short>()),
            }
        );
        Assert.Equal("Fizika", name);
        Assert.Equal(2.33, avg);
    }

    [Fact]
    public void Weakest_NullWhenAllAtLeast35()
    {
        var (name, avg) = SyncSummary.Weakest(
            new (string, IReadOnlyCollection<short>)[]
            {
                ("Matematika", new short[] { 3, 4 }),
                ("Fizika", new short[] { 5 }),
            }
        );
        Assert.Null(name);
        Assert.Null(avg);
        Assert.Equal((null, null), SyncSummary.Weakest(Array.Empty<(string, IReadOnlyCollection<short>)>()));
    }

    [Fact]
    public void NextTest_FirstFromTodayOn()
    {
        var today = new DateOnly(2026, 11, 7);
        var tests = new[]
        {
            new ParsedTest("Fizika", 8, "Fizika", new DateOnly(2026, 11, 20)),
            new ParsedTest("Matematika", 1, "Razlomci", new DateOnly(2026, 11, 7)),
            new ParsedTest("Kemija", null, "Kemija", new DateOnly(2026, 11, 3)),
        };
        Assert.Equal("Matematika", SyncSummary.NextTest(tests, today)!.SubjectName);
        Assert.Null(SyncSummary.NextTest(tests, new DateOnly(2026, 12, 1)));
    }

    private static string Page(params (string ActionId, string Date, string Value)[] grades) =>
        "<html><body>"
        + string.Concat(
            grades.Select(g =>
                $"<div class=\"row \" data-action-id=\"{g.ActionId}\"><div data-date=\"{g.Date}\" class=\"cell\"></div>"
                + $"<div class=\"box\"><div class=\"cell\"><span>{g.Value}</span></div><div class=\"cell\"></div></div></div>"
            )
        )
        + "</body></html>";

    [Fact]
    public async Task Plan_BuildsRowsAndSummary()
    {
        var snapshot = new EdnevnikSnapshot(
            new StudentProfileInfo
            {
                StudentSchool = "OS Ivana Gundulica",
                StudentGrade = "6b",
                ClassMaster = "Ana Anic",
            },
            new List<ScrapedSubject>
            {
                new(
                    new SubjectInfo("Matematika", "Marko Markovic", "2", "100"),
                    Page(("1", "10.10.2026.", "2"), ("2", "12.10.2026.", "3"))
                ),
                new(new SubjectInfo("Glazbena kultura", "N/A", "5", "200"), Page(("3", "10.10.2026.", "5"))),
                new(new SubjectInfo("Sat razrednika", "Ana Anic", "", "250"), null),
                new(new SubjectInfo("Fizika", "Ivo Ivic", "5", "300"), Page(("4", "1.10.2026.", "5"))),
                new(new SubjectInfo("Engleski jezik I", "Eva Evic", "4", "400"), GradePageFixtures.Empty),
            },
            new List<TestInfo>
            {
                new("Fizika", "Gibanje", "20.11."),
                new("Matematika", "", "10.11."),
                new("Matematika", "Stari", "01.10."),
                new("Glazbena kultura", "Pjevanje", "25.11."),
            }
        );

        var lookups = new List<string>();
        var resolver = new SubjectResolver(
            new SubjectMapper(Seed),
            (name, _) =>
            {
                lookups.Add(name);
                return Task.FromResult((short)40);
            }
        );

        var plan = await SyncService.PlanAsync(
            Child,
            snapshot,
            resolver,
            new SchoolYear(2026),
            new DateOnly(2026, 11, 7)
        );

        // nepoznat predmet ide kroz bazu (jednom), sat razrednika se preskače
        Assert.Equal(new[] { "Glazbena kultura" }, lookups);
        Assert.Equal(new short[] { 1, 40, 8, 3 }, plan.ChildSubjects.Select(c => c.SubjectId));
        Assert.Equal("100", plan.ChildSubjects[0].EdnevnikRef);
        Assert.Equal("Marko Markovic", plan.ChildSubjects[0].TeacherName);
        Assert.Equal(4, plan.Grades.Count);
        Assert.Equal(
            GradeRows.Hash($"{Child:D}|action:1|2"),
            plan.Grades.Single(g => g.SubjectId == 1 && g.Value == 2).SourceHash
        );
        Assert.Equal(4, plan.Tests.Count);
        Assert.Equal("Gibanje", plan.Tests[0].Title);
        Assert.Equal((short)8, plan.Tests[0].SubjectId);
        Assert.Equal("Matematika", plan.Tests[1].Title);
        Assert.Equal((short)40, plan.Tests[3].SubjectId);

        var r = plan.Response;
        Assert.Equal("OS Ivana Gundulica", r.School);
        Assert.Equal("6.b", r.ClassName);
        Assert.Equal("Ana Anic", r.HomeroomTeacher);
        Assert.Equal(4, r.SubjectCount);
        Assert.Equal("Matematika", r.WeakestSubject);
        Assert.Equal(2.5, r.WeakestAverage);
        Assert.Equal("Matematika", r.NextTestSubject);
        Assert.Equal("2026-11-10", r.NextTestOn);
        Assert.Empty(r.UnmappedSubjects);
        // bez stranice rasporeda postojeći raspored ostaje
        Assert.Null(plan.Timetable);
    }

    [Fact]
    public async Task Resolver_CachesLookupAndSkipsHomeroom()
    {
        var calls = 0;
        var resolver = new SubjectResolver(
            new SubjectMapper(Seed),
            (_, _) =>
            {
                calls++;
                return Task.FromResult((short)41);
            }
        );

        Assert.Equal((short)41, (await resolver.ResolveAsync("Tjelesna i zdravstvena kultura", default))!.Id);
        Assert.Equal((short)41, (await resolver.ResolveAsync(" Tjelesna i zdravstvena kultura ", default))!.Id);
        Assert.Equal((short)3, (await resolver.ResolveAsync("Engleski jezik I", default))!.Id);
        Assert.Null(await resolver.ResolveAsync("Sat razrednika", default));
        Assert.Null(await resolver.ResolveAsync("SAT RAZREDNIKA", default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Access_ParentOrSelfManagedStudent()
    {
        var caller = Guid.NewGuid();
        var kid = new ChildRecord(Child, Guid.NewGuid(), caller, SelfManaged: true);
        var notSelf = kid with { SelfManaged = false };

        Assert.True(SyncService.CanSync(kid, caller, callerIsParentInFamily: false));
        Assert.False(SyncService.CanSync(notSelf, caller, callerIsParentInFamily: false));
        Assert.True(SyncService.CanSync(notSelf, Guid.NewGuid(), callerIsParentInFamily: true));
        Assert.False(SyncService.CanSync(kid, Guid.NewGuid(), callerIsParentInFamily: false));
    }

    [Fact]
    public void BuildCookies_ScopedToOcjeneRoot()
    {
        var jar = EdnevnikSyncScraper.BuildCookies(
            new[]
            {
                new SyncCookie { Name = "PHPSESSID", Value = "x" },
            }
        );
        var cookies = jar.GetCookies(new Uri("https://ocjene.skole.hr/course"));
        Assert.Single(cookies);
        Assert.Empty(jar.GetCookies(new Uri("https://example.com/")));
    }
}

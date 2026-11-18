using System.Net;
using E_Dnevnik_API.Controllers;
using E_Dnevnik_API.V2.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RichardSzalay.MockHttp;

namespace E_Dnevnik_API.Tests.Unit;

public class V2AiTests
{
    private static readonly Guid Caller = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Asker Free = new(Guid.NewGuid(), Guid.NewGuid(), 5, "os", "free");

    private sealed class FakeRepo : IAiRepository
    {
        public Asker? Asker = Free;
        public int Asked;
        public readonly List<(string Question, AskMessage Answer)> Saved = new();
        public readonly List<bool> Calls = new();
        public bool IsConfigured => true;

        public Task<Asker?> GetAskerAsync(Guid authUserId, CancellationToken ct) => Task.FromResult(Asker);

        public Task<int> CountQuestionsAsync(Guid childId, DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult(Asked);

        public Task<List<ChatTurn>> RecentAsync(Guid childId, DateTimeOffset since, int limit, CancellationToken ct) =>
            Task.FromResult(new List<ChatTurn> { new(true, "Što je razlomak?"), new(false, "Dio cjeline.") });

        public Task SaveAsync(Asker asker, string question, AskMessage answer, CancellationToken ct)
        {
            Saved.Add((question, answer));
            return Task.CompletedTask;
        }

        public PhotoHelpRules Rules = PhotoHelpRules.Default;
        public int Photos;
        public readonly List<PhotoResult> SavedPhotos = new();
        public readonly List<AiCall> Logged = new();

        public Task LogCallAsync(AiCall call, CancellationToken ct)
        {
            Calls.Add(call.Ok);
            Logged.Add(call);
            return Task.CompletedTask;
        }

        public Task<PhotoHelpRules> GetPhotoRulesAsync(Guid childId, CancellationToken ct) => Task.FromResult(Rules);

        public Task<int> CountPhotosAsync(Guid childId, DateTimeOffset since, CancellationToken ct) =>
            Task.FromResult(Photos);

        public Task<Guid> SavePhotoAsync(Asker asker, string subject, PhotoResult result, CancellationToken ct)
        {
            SavedPhotos.Add(result);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class FakeAi : IAiChatClient
    {
        public bool Fail;
        public IReadOnlyList<ChatTurn>? History;
        public string? System;
        public bool IsConfigured => true;
        public string Provider => "test";

        public Task<AiReply> AskAsync(string system, IReadOnlyList<ChatTurn> history, string question, CancellationToken ct)
        {
            if (Fail)
                throw new HttpRequestException("down");
            System = system;
            History = history;
            return Task.FromResult(new AiReply(new AskMessage { Text = "Evo:", Steps = { "Korak" } }, 10, 5));
        }

        public string PhotoContent = PhotoJson;
        public string? PhotoSystem;

        public Task<AiCompletion> ReadImageAsync(string system, string instruction, byte[] image, string mime, CancellationToken ct)
        {
            if (Fail)
                throw new HttpRequestException("down");
            PhotoSystem = system;
            return Task.FromResult(new AiCompletion(PhotoContent, 900, 300));
        }

        public string WriteContent = ExplainJson;
        public string? WriteInstruction;

        public string? WriteSystem;

        public Task<AiCompletion> WriteAsync(string system, string instruction, CancellationToken ct)
        {
            if (Fail)
                throw new HttpRequestException("down");
            WriteSystem = system;
            WriteInstruction = instruction;
            return Task.FromResult(new AiCompletion(WriteContent, 400, 300, "m"));
        }
    }

    private const string ExplainJson = """
        Evo: {"ok": true, "subject": "math", "title": "Zajednički nazivnik", "topic": "Razlomci",
         "sections": [{"title": "Što je to?", "lines": ["Broj koji dijele oba nazivnika.", ""]},
                      {"title": "", "lines": ["bez naslova se odbacuje"]},
                      {"title": "Primjer", "lines": ["{1/2} + {1/4} = {3/4}"]},
                      {"title": "Pazi!", "lines": ["Brojnike ne množi bez nazivnika."], "warning": true}],
         "joso_says": "Možeš ti to!"}
        """;

    private sealed class FakeExplainRepo : IExplainRepository
    {
        public HelpContext? Help = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Luka", 5, "primary", "free", "Razlomci", null);
        public ExistingNote? Existing;
        public int Explained;
        public readonly List<(ExplainNote Note, string Detail)> Saved = new();
        public bool IsConfigured => true;

        public Task<HelpContext?> GetContextAsync(Guid parentId, Guid notificationId, CancellationToken ct) => Task.FromResult(Help);

        public Task<ExistingNote?> FindNoteAsync(Guid familyId, string source, CancellationToken ct) => Task.FromResult(Existing);

        public Task<int> CountExplanationsAsync(Guid familyId, DateTimeOffset since, CancellationToken ct) => Task.FromResult(Explained);

        public Task<Guid> SaveNoteAsync(HelpContext help, ExplainNote note, string detail, CancellationToken ct)
        {
            Saved.Add((note, detail));
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private static ExplainService Explains(FakeExplainRepo repo, FakeRepo calls, FakeAi ai) =>
        new(repo, calls, ai, TimeProvider.System, NullLogger<ExplainService>.Instance);

    [Fact]
    public async Task Explain_FirstIsFreeAndWritesTheNote()
    {
        var (repo, calls, ai) = (new FakeExplainRepo(), new FakeRepo(), new FakeAi());
        var outcome = await Explains(repo, calls, ai).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Equal(ExplainOutcomeKind.Ok, outcome.Kind);
        Assert.True(outcome.Response!.WasFree);
        Assert.Equal(3, outcome.Response.Steps);
        var (note, detail) = Assert.Single(repo.Saved);
        Assert.Equal("Zajednički nazivnik", note.Title);
        Assert.Equal(1, note.SubjectId);
        Assert.True(note.Sections[2]!["warning"]!.GetValue<bool>());
        Assert.Equal("Objasnio Joso · 3 dijela", detail);
        Assert.Contains("Razlomci", ai.WriteInstruction);
        Assert.DoesNotContain("Luka", ai.WriteSystem + ai.WriteInstruction);
        Assert.Equal(new[] { true }, calls.Calls);
    }

    [Fact]
    public async Task Explain_FreePlanNeedsPremiumAfterTheFirst()
    {
        var (repo, ai) = (new FakeExplainRepo { Explained = 1 }, new FakeAi());
        var outcome = await Explains(repo, new FakeRepo(), ai).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Equal(ExplainOutcomeKind.PremiumRequired, outcome.Kind);
        Assert.Null(ai.WriteInstruction);
    }

    [Fact]
    public async Task Explain_PremiumIsNotFreeAndHasADailyCap()
    {
        var repo = new FakeExplainRepo();
        repo.Help = repo.Help! with { Plan = "premium" };
        repo.Explained = 3;
        var ok = await Explains(repo, new FakeRepo(), new FakeAi()).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.False(ok.Response!.WasFree);

        repo.Explained = ExplainService.PremiumDailyCap;
        var capped = await Explains(repo, new FakeRepo(), new FakeAi()).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.Equal(ExplainOutcomeKind.LimitReached, capped.Kind);
    }

    [Fact]
    public async Task Explain_SecondTapReturnsTheSameNote()
    {
        var id = Guid.NewGuid();
        var (repo, ai) = (new FakeExplainRepo { Existing = new ExistingNote(id, "Razlomci", 2), Explained = 1 }, new FakeAi());
        var outcome = await Explains(repo, new FakeRepo(), ai).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Equal(id, outcome.Response!.NoteId);
        Assert.Null(ai.WriteInstruction);
        Assert.Empty(repo.Saved);
    }

    [Fact]
    public async Task Explain_OtherFamilyOrNothingToGoOn()
    {
        var notFound = await Explains(new FakeExplainRepo { Help = null }, new FakeRepo(), new FakeAi())
            .ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.Equal(ExplainOutcomeKind.NotFound, notFound.Kind);

        var repo = new FakeExplainRepo();
        repo.Help = repo.Help! with { Topic = null };
        var nothing = await Explains(repo, new FakeRepo(), new FakeAi()).ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);
        Assert.Equal(ExplainOutcomeKind.NothingToExplain, nothing.Kind);
    }

    [Fact]
    public async Task Explain_RefusalIsLoggedButNotSaved()
    {
        var (repo, calls) = (new FakeExplainRepo(), new FakeRepo());
        var outcome = await Explains(repo, calls, new FakeAi { WriteContent = """{"ok": false}""" })
            .ExplainAsync(Guid.NewGuid(), Guid.NewGuid(), default);

        Assert.Equal(ExplainOutcomeKind.AiUnavailable, outcome.Kind);
        Assert.Empty(repo.Saved);
        Assert.Equal(new[] { false }, calls.Calls);
    }

    private const string PhotoJson = """
        {"readable": true, "expression": "{3/4} + {1/6} = ?", "topic": "Razlomci", "answer": "{3/4} + {1/6} = {11/12}",
         "steps": [{"title": "Nađi zajednički nazivnik", "text": "4 i 6 imaju višekratnik 12.", "formulas": ["{3/4} = {9/12} (× 3)"],
                    "why": {"title": "Zašto 12?", "text": "Najmanji je.", "formula": "4 · 3 = 12", "tip": "Kao pizza."}},
                   {"title": "", "text": "bez naslova se odbacuje"},
                   {"title": "Zbroji", "formulas": ["{9/12} + {2/12} = {11/12}"]}],
         "similar": [{"expression": "{1/2} + {1/4} = ?", "difficulty": "easy"}, {"expression": "{2/3} + {1/6} = ?", "difficulty": "weird"}]}
        """;

    private static PhotoService Photos(FakeRepo repo, FakeAi ai) =>
        new(repo, ai, TimeProvider.System, NullLogger<PhotoService>.Instance);

    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

    [Fact]
    public async Task Photo_ExplainsAndSavesWithoutThePicture()
    {
        var repo = new FakeRepo();
        var ai = new FakeAi();
        var outcome = await Photos(repo, ai).ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default);

        Assert.Equal(PhotoOutcomeKind.Ok, outcome.Kind);
        Assert.Equal("Razlomci", outcome.Response!.Topic);
        Assert.Equal(2, outcome.Response.Steps.Count);
        Assert.Equal("medium", outcome.Response.SimilarTasks[1]!["difficulty"]!.GetValue<string>());
        Assert.Single(repo.SavedPhotos);
        Assert.Equal(("photo", 1, true), (repo.Logged[0].Feature, repo.Logged[0].Images, repo.Logged[0].Ok));
        Assert.Contains("Matematika", ai.PhotoSystem);
    }

    [Fact]
    public async Task Photo_FollowsTheParentsRules()
    {
        var off = new FakeRepo { Rules = PhotoHelpRules.Default with { Enabled = false } };
        Assert.Equal(PhotoOutcomeKind.PhotoHelpOff, (await Photos(off, new FakeAi()).ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default)).Kind);

        var onlyCroatian = new FakeRepo { Rules = PhotoHelpRules.Default with { Subjects = new short[] { 2 } } };
        Assert.Equal(PhotoOutcomeKind.SubjectNotAllowed, (await Photos(onlyCroatian, new FakeAi()).ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default)).Kind);
        Assert.Equal(PhotoOutcomeKind.Ok, (await Photos(onlyCroatian, new FakeAi()).ExplainAsync(Caller, "croatian", Jpeg, "image/jpeg", default)).Kind);

        var capped = new FakeRepo { Rules = PhotoHelpRules.Default with { DailyCap = 3 }, Photos = 3 };
        var ai = new FakeAi();
        Assert.Equal(PhotoOutcomeKind.LimitReached, (await Photos(capped, ai).ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default)).Kind);
        Assert.Null(ai.PhotoSystem);

        var hints = new FakeRepo { Rules = PhotoHelpRules.Default with { Level = "hint" } };
        var hintAi = new FakeAi();
        await Photos(hints, hintAi).ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default);
        Assert.Contains("BEZ konačnog rješenja", hintAi.PhotoSystem);
    }

    [Fact]
    public async Task Photo_UnreadableIsLoggedButNotSaved()
    {
        var repo = new FakeRepo();
        var outcome = await Photos(repo, new FakeAi { PhotoContent = "{\"readable\": false}" })
            .ExplainAsync(Caller, "math", Jpeg, "image/jpeg", default);

        Assert.Equal(PhotoOutcomeKind.Unreadable, outcome.Kind);
        Assert.Empty(repo.SavedPhotos);
        Assert.Equal(new[] { false }, repo.Calls);
    }

    [Theory]
    [InlineData("nije json")]
    [InlineData("{\"readable\": true, \"expression\": \"2 + 2\", \"steps\": []}")]
    [InlineData("{\"readable\": true, \"steps\": [{\"title\": \"x\"}]}")]
    public void PhotoParser_RejectsAnswersWithoutTaskOrSteps(string raw) => Assert.Null(PhotoAnswerParser.Parse(raw));

    [Fact]
    public void DecodeImage_AcceptsOnlyJpegAndPng()
    {
        Assert.Equal("image/jpeg", V2AiController.DecodeImage(Convert.ToBase64String(Jpeg))!.Value.Mime);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 };
        Assert.Equal("image/png", V2AiController.DecodeImage(Convert.ToBase64String(png))!.Value.Mime);
        Assert.Null(V2AiController.DecodeImage(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 })));
        Assert.Null(V2AiController.DecodeImage("%%%"));
        Assert.Null(V2AiController.DecodeImage(null));
    }

    [Fact]
    public void PhotoSubjects_NatureCoversItsSubjectIds()
    {
        Assert.True(PhotoSubjects.Allowed("nature", new short[] { 5 }));
        Assert.False(PhotoSubjects.Allowed("english", new short[] { 1, 2 }));
        Assert.True(PhotoSubjects.Allowed("english", null));
    }

    private static AskService Service(FakeRepo repo, FakeAi ai) =>
        new(repo, ai, Options.Create(new AiOptions()), TimeProvider.System, NullLogger<AskService>.Instance);

    [Fact]
    public async Task Ask_AnswersSavesAndCountsDown()
    {
        var repo = new FakeRepo { Asked = 2 };
        var ai = new FakeAi();
        var outcome = await Service(repo, ai).AskAsync(Caller, "Kako zbrojiti razlomke?", default);

        Assert.Equal(AskOutcomeKind.Ok, outcome.Kind);
        Assert.Equal("Evo:", outcome.Response!.Messages.Single().Text);
        Assert.Equal(2, outcome.Response.QuestionsLeft);
        Assert.Single(repo.Saved);
        Assert.Equal(new[] { true }, repo.Calls);
        Assert.Equal(2, ai.History!.Count);
        Assert.Contains("5. razreda osnovne škole", ai.System);
    }

    [Fact]
    public async Task Ask_StopsAtTheFreeLimitWithoutCallingTheModel()
    {
        var repo = new FakeRepo { Asked = 5 };
        var ai = new FakeAi();
        var outcome = await Service(repo, ai).AskAsync(Caller, "Pitanje", default);

        Assert.Equal(AskOutcomeKind.LimitReached, outcome.Kind);
        Assert.Null(ai.History);
        Assert.Empty(repo.Calls);
    }

    [Fact]
    public async Task Ask_PremiumHasNoCountButAHighCap()
    {
        var premium = Free with { Plan = "premium" };
        var ok = await Service(new FakeRepo { Asker = premium, Asked = 20 }, new FakeAi()).AskAsync(Caller, "x", default);
        Assert.Null(ok.Response!.QuestionsLeft);

        var capped = await Service(new FakeRepo { Asker = premium, Asked = 50 }, new FakeAi()).AskAsync(Caller, "x", default);
        Assert.Equal(AskOutcomeKind.LimitReached, capped.Kind);
    }

    [Fact]
    public async Task Ask_FailedModelIsLoggedButNotSavedOrCounted()
    {
        var repo = new FakeRepo();
        var outcome = await Service(repo, new FakeAi { Fail = true }).AskAsync(Caller, "x", default);

        Assert.Equal(AskOutcomeKind.AiUnavailable, outcome.Kind);
        Assert.Empty(repo.Saved);
        Assert.Equal(new[] { false }, repo.Calls);
    }

    [Fact]
    public async Task Ask_ParentAccountIsNotAChild()
    {
        var outcome = await Service(new FakeRepo { Asker = null }, new FakeAi()).AskAsync(Caller, "x", default);
        Assert.Equal(AskOutcomeKind.NotAChild, outcome.Kind);
    }

    [Theory]
    [InlineData("{\"text\": \"Prvo isti nazivnik.\", \"steps\": [\"Nađi višekratnik\", \" \", \"Zbroji\"]}", "Prvo isti nazivnik.", 2)]
    [InlineData("Evo: ```json\n{\"text\": \"Bok\", \"steps\": []}\n```", "Bok", 0)]
    [InlineData("Samo običan tekst.", "Samo običan tekst.", 0)]
    [InlineData("{\"answer\": 1}", "{\"answer\": 1}", 0)]
    public void Parser_ReadsJsonOrFallsBackToText(string raw, string text, int steps)
    {
        var m = AskAnswerParser.Parse(raw);
        Assert.Equal(text, m.Text);
        Assert.Equal(steps, m.Steps.Count);
    }

    [Fact]
    public void Parser_CutsVeryLongAnswers()
    {
        var m = AskAnswerParser.Parse(new string('a', 5000));
        Assert.True(m.Text.Length <= AskAnswerParser.MaxText + 1);
    }

    [Theory]
    [InlineData("2026-11-18T05:59:00Z", "2026-11-17T07:00:00+01:00")] // 6:59 u Zagrebu: još jučerašnji dan
    [InlineData("2026-11-18T06:00:00Z", "2026-11-18T07:00:00+01:00")]
    [InlineData("2026-07-01T04:30:00Z", "2026-06-30T07:00:00+02:00")] // ljetno vrijeme: 6:30
    public void AskDay_StartsAtSevenInZagreb(string now, string start) =>
        Assert.Equal(DateTimeOffset.Parse(start), AskDay.StartOf(DateTimeOffset.Parse(now)));

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Što je razlomak?  ", "Što je razlomak?")]
    public void Clean_TrimsAndRejectsEmpty(string? q, string? expected) => Assert.Equal(expected, V2AiController.Clean(q));

    [Fact]
    public void Clean_RejectsTooLong() => Assert.Null(V2AiController.Clean(new string('a', 501)));

    [Fact]
    public async Task ChatClient_SendsOpenAiRequestAndReadsTokens()
    {
        var http = new MockHttpMessageHandler();
        http.Expect(HttpMethod.Post, "http://127.0.0.1:3001/v1/chat/completions")
            .WithHeaders("Authorization", "Bearer freellmapi-test")
            .WithPartialContent("\"role\":\"system\"")
            .WithPartialContent("\"model\":\"gemini-3.1-flash-lite\"")
            .Respond(
                "application/json",
                "{\"choices\":[{\"message\":{\"content\":\"{\\\"text\\\":\\\"Bok\\\",\\\"steps\\\":[\\\"a\\\"]}\"}}],"
                    + "\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":30}}"
            );
        var client = new AiChatClient(new Factory(http), Options.Create(new AiOptions { ApiKey = "freellmapi-test" }));

        var reply = await client.AskAsync("sys", new List<ChatTurn>(), "Bok?", default);

        Assert.Equal("Bok", reply.Message.Text);
        Assert.Equal(new[] { "a" }, reply.Message.Steps);
        Assert.Equal((120, 30), (reply.InputTokens, reply.OutputTokens));
        http.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public async Task ChatClient_ErrorStatusThrows()
    {
        var http = new MockHttpMessageHandler();
        http.When("*").Respond(HttpStatusCode.TooManyRequests);
        var client = new AiChatClient(new Factory(http), Options.Create(new AiOptions { ApiKey = "k" }));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.AskAsync("s", new List<ChatTurn>(), "q", default));
    }

    [Fact]
    public async Task ChatClient_FallsBackToTheNextModel()
    {
        var http = new MockHttpMessageHandler();
        http.Expect("*").WithPartialContent("\"model\":\"m1\"").Respond(HttpStatusCode.ServiceUnavailable);
        http.Expect("*")
            .WithPartialContent("\"model\":\"m2\"")
            .Respond("application/json", "{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"length\"}]}");
        http.Expect("*")
            .WithPartialContent("\"model\":\"m3\"")
            .Respond("application/json", "{\"choices\":[{\"message\":{\"content\":\"Bok\"},\"finish_reason\":\"stop\"}]}");
        var client = new AiChatClient(new Factory(http), Options.Create(new AiOptions { ApiKey = "k", ChatModels = "m1, auto, m2,m3" }));

        var reply = await client.AskAsync("s", new List<ChatTurn>(), "q", default);

        Assert.Equal(("Bok", "m3"), (reply.Message.Text, reply.Model));
        http.VerifyNoOutstandingExpectation();
    }

    [Fact]
    public void Models_NeverAuto() =>
        Assert.Equal(new[] { "a", "b" }, AiOptions.Split(" a, auto ,auto:fast, b ,"));

    private sealed class Factory(MockHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.ToHttpClient();
    }
}

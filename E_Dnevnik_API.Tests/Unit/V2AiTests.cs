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

        public Task LogCallAsync(Guid f, string p, bool ok, int i, int o, int l, CancellationToken ct)
        {
            Calls.Add(ok);
            return Task.CompletedTask;
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

    private sealed class Factory(MockHttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.ToHttpClient();
    }
}

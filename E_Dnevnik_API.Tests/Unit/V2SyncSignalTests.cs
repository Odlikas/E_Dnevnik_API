using E_Dnevnik_API.V2.Push;
using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.Tests.Unit;

// T9 (D331): the kid phone gets a data-only 'kid_sync' push when the parent changes something.
public class V2SyncSignalTests
{
    private static SyncSignal Signal(string reason = "apps", string? token = "kid-fid", bool revoked = false) =>
        new(Guid.NewGuid(), reason, token, revoked);

    [Fact]
    public void OneSilentPushPerPhoneWithTheReason()
    {
        var p = SyncSignalWorker.Plan(new[] { Signal("grant") }).Single();
        Assert.True(p.Silent);
        Assert.Equal("kid-fid", p.Token);
        Assert.Equal(RecipientKind.Device, p.Kind);
        Assert.Equal("kid_sync", p.Data["type"]);
        Assert.Equal("grant", p.Data["reason"]);
    }

    [Fact]
    public void PhonesWithoutTokenAreSkipped()
    {
        Assert.Empty(SyncSignalWorker.Plan(new[] { Signal(token: null), Signal(token: "") }));
    }

    [Fact]
    public void RevokedPhoneGetsOnlyUnpaired()
    {
        Assert.Empty(SyncSignalWorker.Plan(new[] { Signal("apps", revoked: true) }));
        Assert.Single(SyncSignalWorker.Plan(new[] { Signal("unpaired", revoked: true) }));
    }

    [Fact]
    public void DataMessageHasNoNotificationAndWakesTheApp()
    {
        var m = FcmPushSender.ToMessage(SyncSignalWorker.Plan(new[] { Signal() }).Single());
        Assert.Null(m.Notification);
        Assert.Equal("kid_sync", m.Data["type"]);
        Assert.Equal(TimeSpan.FromMinutes(10), m.Android.TimeToLive);
        Assert.Equal("kid_sync", m.Android.CollapseKey);
        Assert.Equal("background", m.Apns.Headers["apns-push-type"]);
        Assert.Equal("5", m.Apns.Headers["apns-priority"]);
        Assert.True(m.Apns.Aps.ContentAvailable);
    }

    [Fact]
    public void TakeDueSqlStampsOnlyChangedRowsOfTheLastTenMinutes()
    {
        var sql = SyncSignalRepository.TakeDueSql;
        Assert.Contains("sent_at is null or sent_at < requested_at", sql);
        Assert.Contains("interval '10 minutes'", sql);
        Assert.Contains("for update skip locked", sql);
        Assert.Contains("set sent_at = now()", sql);
    }

    private sealed class FakeRepo : ISyncSignalRepository
    {
        public IReadOnlyList<SyncSignal> Due = Array.Empty<SyncSignal>();
        public List<string>? Cleared;
        public bool IsConfigured => true;

        public Task<IReadOnlyList<SyncSignal>> TakeDueAsync(int limit, CancellationToken ct) => Task.FromResult(Due);

        public Task ClearDeviceTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken ct)
        {
            Cleared = tokens.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSender : IPushSender
    {
        public Func<OutgoingPush, SendStatus> Status = _ => SendStatus.Sent;
        public List<OutgoingPush> Sent = new();
        public bool IsConfigured => true;

        public Task<IReadOnlyList<SendResult>> SendAsync(IReadOnlyList<OutgoingPush> pushes, CancellationToken ct)
        {
            Sent.AddRange(pushes);
            return Task.FromResult<IReadOnlyList<SendResult>>(pushes.Select(p => new SendResult(p.Token, Status(p))).ToList());
        }
    }

    [Fact]
    public async Task RoundSendsAndClearsDeadTokens()
    {
        var repo = new FakeRepo { Due = new[] { Signal(token: "a"), Signal(token: "b"), Signal(token: null) } };
        var sender = new FakeSender { Status = p => p.Token == "b" ? SendStatus.DeadToken : SendStatus.Sent };
        var worker = new SyncSignalWorker(repo, sender, NullLogger<SyncSignalWorker>.Instance);

        Assert.Equal(3, await worker.RunOnceAsync(CancellationToken.None));
        Assert.Equal(new[] { "a", "b" }, sender.Sent.Select(p => p.Token));
        Assert.Equal(new[] { "b" }, repo.Cleared);
    }

    [Fact]
    public async Task NothingDueSendsNothing()
    {
        var repo = new FakeRepo();
        var sender = new FakeSender();
        Assert.Equal(0, await new SyncSignalWorker(repo, sender, NullLogger<SyncSignalWorker>.Instance)
            .RunOnceAsync(CancellationToken.None));
        Assert.Empty(sender.Sent);
        Assert.Null(repo.Cleared);
    }
}

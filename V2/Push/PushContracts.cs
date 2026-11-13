namespace E_Dnevnik_API.V2.Push
{
    // jedan neposlani red iz public.notifications, s imenima koja trebaju za tekst
    public record PendingPush(
        Guid Id,
        Guid FamilyId,
        Guid? ParentId,
        Guid? ChildId,
        string Type,
        string? ChildFirstName,
        string? SubjectName,
        short? Value,
        string? Note,
        string? GradingElement,
        // zahtjev za vrijeme (payload 'request_id'), za time_request i time_declined
        Guid? RequestId = null
    );

    public enum RecipientKind
    {
        Parent,
        Device,
    }

    // roditelj (parents.push_token) ili djetetov trenutni mobitel (devices.push_token, revoked_at is null)
    public record PushRecipient(RecipientKind Kind, Guid Id, Guid FamilyId, Guid? ChildId, string Token);

    // jedna FCM poruka; Notifications su redovi koje pokriva (više ocjena odjednom = jedna poruka)
    public record OutgoingPush(
        string Token,
        RecipientKind Kind,
        string Title,
        string Body,
        IReadOnlyDictionary<string, string> Data,
        IReadOnlyList<Guid> Notifications,
        // samo podaci, bez obavijesti: mobitel ga ne prikaže, samo povuče stanje (kid_sync, T9)
        bool Silent = false
    );

    public enum SendStatus
    {
        Sent,

        // FCM kaže da token više ne vrijedi: brišemo ga
        DeadToken,

        // trajna ili nepoznata greška; red se ipak označi poslanim
        Failed,

        // FCM je privremeno nedostupan (Unavailable, Internal, QuotaExceeded): red ostaje
        // neposlan i ide u sljedećem krugu (D314)
        Retry,
    }

    public record SendResult(string Token, SendStatus Status);

    // djetetov mobitel koji treba odmah povući stanje blokiranja (public.device_sync_signals, T9)
    public record SyncSignal(Guid DeviceId, string Reason, string? Token, bool Revoked);

    public interface ISyncSignalRepository
    {
        bool IsConfigured { get; }

        // označi sent_at na dospjelim redovima i vrati ih (najviše jednom; izgubljeni push pokrije
        // mobitelovo povlačenje svake 2 min)
        Task<IReadOnlyList<SyncSignal>> TakeDueAsync(int limit, CancellationToken ct);

        Task ClearDeviceTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken ct);
    }

    // zaključani redovi (for update skip locked) dok traje slanje; Dispose bez Complete = rollback
    public interface IPushClaim : IAsyncDisposable
    {
        IReadOnlyList<PendingPush> Rows { get; }
        IReadOnlyList<PushRecipient> Recipients { get; }

        Task CompleteAsync(IReadOnlyCollection<Guid> pushed, IReadOnlyCollection<string> deadTokens, CancellationToken ct);
    }

    public interface IPushRepository
    {
        bool IsConfigured { get; }
        Task<IPushClaim> ClaimAsync(IReadOnlyCollection<string> types, int limit, CancellationToken ct);
    }

    public interface IPushSender
    {
        // false kad Firebase za v2 nije postavljen (nema FIREBASE_V2_* varijable)
        bool IsConfigured { get; }
        Task<IReadOnlyList<SendResult>> SendAsync(IReadOnlyList<OutgoingPush> pushes, CancellationToken ct);
    }
}

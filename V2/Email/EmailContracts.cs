using System.Text.Json.Nodes;

namespace E_Dnevnik_API.V2.Email
{
    // jedan neposlani red iz public.email_outbox; Attempts = broj dosadašnjih neuspjelih slanja
    // (zapisan na početku stupca error, "3|poruka"), jer ednevnik_service smije mijenjati samo sent_at i error
    public record PendingEmail(long Id, string Kind, string To, JsonObject Payload, int Attempts, DateTimeOffset CreatedAt);

    // gotov e-mail za Resend
    public record RenderedEmail(string Subject, string Text, string Html);

    public enum EmailSendStatus
    {
        Sent,

        // privremeno (mreža, 429, 5xx, ključ ili domena nisu dobro postavljeni): pokušaj ponovno kasnije
        Retry,

        // Resend odbija baš ovaj e-mail (npr. nevaljana adresa): više se ne šalje
        Failed,
    }

    public record EmailSendResult(EmailSendStatus Status, string? Error = null);

    // ishod jednog reda: Sent -> sent_at; inače error s novim brojem pokušaja
    public record EmailOutcome(long Id, bool Sent, int Attempts, string? Error);

    // zaključani redovi (for update skip locked) dok traje slanje; Dispose bez Complete = rollback
    public interface IEmailClaim : IAsyncDisposable
    {
        IReadOnlyList<PendingEmail> Rows { get; }

        Task CompleteAsync(IReadOnlyCollection<EmailOutcome> outcomes, CancellationToken ct);
    }

    public interface IEmailOutboxRepository
    {
        bool IsConfigured { get; }

        // skip: redovi koji još čekaju sljedeći pokušaj (backoff u memoriji radnika)
        Task<IEmailClaim> ClaimAsync(
            IReadOnlyCollection<string> kinds,
            int limit,
            int maxAttempts,
            IReadOnlyCollection<long> skip,
            CancellationToken ct
        );
    }

    public interface IEmailSender
    {
        // false kad nema RESEND_API_KEY
        bool IsConfigured { get; }

        // idempotencyKey: Resend isti ključ 24 h ne šalje dvaput (pad usred kruga ne šalje dupli e-mail)
        Task<EmailSendResult> SendAsync(string to, RenderedEmail email, string idempotencyKey, CancellationToken ct);
    }
}

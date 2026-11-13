namespace E_Dnevnik_API.V2.Push
{
    // svake 2 s: djetetovim mobitelima kojima se nešto promijenilo (vrijeme, aplikacije, dogovor,
    // "Dopusti promjene", odspajanje) pošalje tihi 'kid_sync' push i mobitel odmah povuče stanje
    // blokiranja (spec T9, D331). Bez Supabasea ili Firebasea v2 se samo ugasi.
    public class SyncSignalWorker : BackgroundService
    {
        public const string Type = "kid_sync";
        public const string Unpaired = "unpaired";
        public const int BatchSize = 200;
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

        private readonly ISyncSignalRepository _repo;
        private readonly IPushSender _sender;
        private readonly ILogger<SyncSignalWorker> _logger;

        public SyncSignalWorker(ISyncSignalRepository repo, IPushSender sender, ILogger<SyncSignalWorker> logger)
        {
            _repo = repo;
            _sender = sender;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // razlog gašenja već zapiše PushWorker
            if (!_repo.IsConfigured || !_sender.IsConfigured)
                return;

            using var timer = new PeriodicTimer(Interval);
            try
            {
                do
                {
                    try
                    {
                        while (await RunOnceAsync(stoppingToken) >= BatchSize && !stoppingToken.IsCancellationRequested) { }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning("[sync push] krug nije uspio: {Type}: {Message}", ex.GetType().Name, ex.Message);
                    }
                } while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }

        // jedan krug; vraća broj uzetih redova
        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            var due = await _repo.TakeDueAsync(BatchSize, ct);
            var pushes = Plan(due);
            if (pushes.Count == 0)
                return due.Count;

            var results = await _sender.SendAsync(pushes, ct);
            var dead = results.Where(r => r.Status == SendStatus.DeadToken).Select(r => r.Token).ToList();
            if (dead.Count > 0)
                await _repo.ClearDeviceTokensAsync(dead, ct);
            _logger.LogInformation(
                "[sync push] {Sent}/{Count} poslano, {Dead} nevaljanih tokena obrisano.",
                results.Count(r => r.Status == SendStatus.Sent),
                pushes.Count,
                dead.Count
            );
            return due.Count;
        }

        // jedan tihi push po mobitelu s tokenom; odspojenom samo 'unpaired'
        public static IReadOnlyList<OutgoingPush> Plan(IEnumerable<SyncSignal> signals) =>
            signals
                .Where(s => !string.IsNullOrEmpty(s.Token) && (!s.Revoked || s.Reason == Unpaired))
                .Select(s => new OutgoingPush(
                    s.Token!,
                    RecipientKind.Device,
                    "",
                    "",
                    new Dictionary<string, string> { ["type"] = Type, ["reason"] = s.Reason },
                    Array.Empty<Guid>(),
                    Silent: true
                ))
                .ToList();
    }
}

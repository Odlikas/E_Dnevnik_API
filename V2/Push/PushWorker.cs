namespace E_Dnevnik_API.V2.Push
{
    // svakih ~15 s: zaključa neposlane obavijesti, pošalje ih kroz FCM roditeljima i djetetovu
    // mobitelu, označi pushed_at i obriše tokene koje FCM odbije. Bez Supabasea ili Firebasea v2
    // jednom zapiše zašto ne radi i završi (aplikacija radi dalje).
    public class PushWorker : BackgroundService
    {
        public const string DetailsVar = "V2_PUSH_DETAILS";
        public const int BatchSize = 20;
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

        private readonly IPushRepository _repo;
        private readonly IPushSender _sender;
        private readonly ILogger<PushWorker> _logger;
        private readonly bool _details;

        public PushWorker(IPushRepository repo, IPushSender sender, IConfiguration config, ILogger<PushWorker> logger)
        {
            _repo = repo;
            _sender = sender;
            _logger = logger;
            _details = string.Equals(config[DetailsVar], "true", StringComparison.OrdinalIgnoreCase);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_repo.IsConfigured)
            {
                _logger.LogInformation("[push] isključen: nema ConnectionStrings:Supabase.");
                return;
            }
            if (!_sender.IsConfigured)
            {
                _logger.LogInformation(
                    "[push] isključen: nema {File} ni {Json}.",
                    FcmPushSender.CredentialsFileVar,
                    FcmPushSender.CredentialsJsonVar
                );
                return;
            }
            _logger.LogInformation("[push] radi, svakih {Seconds} s (detalji u tekstu: {Details}).",
                Interval.TotalSeconds, _details);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    // dok ima punih serija, odmah sljedeća
                    while (await RunOnceAsync(stoppingToken) >= BatchSize && !stoppingToken.IsCancellationRequested) { }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // redovi ostaju neposlani (rollback) i idu u sljedećem krugu
                    _logger.LogWarning("[push] krug nije uspio: {Type}: {Message}", ex.GetType().Name, ex.Message);
                }
            } while (await WaitAsync(timer, stoppingToken));
        }

        private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
        {
            try
            {
                return await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // jedan krug; vraća broj zaključanih redova
        public async Task<int> RunOnceAsync(CancellationToken ct)
        {
            await using var claim = await _repo.ClaimAsync(PushPlanner.KnownTypes, BatchSize, ct);
            if (claim.Rows.Count == 0)
                return 0;

            var pushes = PushPlanner.Plan(claim.Rows, claim.Recipients, _details);
            // ako FCM baci iznimku (mreža, vjerodajnice), Dispose radi rollback i red čeka sljedeći krug
            var results = pushes.Count == 0 ? Array.Empty<SendResult>() : await _sender.SendAsync(pushes, ct);
            var dead = results.Where(r => r.Status == SendStatus.DeadToken).Select(r => r.Token).Distinct().ToList();

            // svaki zaključani red je obrađen, i kad nema kome poslati (nitko nema token), osim reda
            // kojem su sva slanja privremeno pala: on čeka sljedeći krug (D314)
            var retry = RetryRows(pushes, results);
            await claim.CompleteAsync(claim.Rows.Select(r => r.Id).Where(id => !retry.Contains(id)).ToList(), dead, ct);
            _logger.LogInformation(
                "[push] {Rows} obavijesti, {Sent} poslano, {Dead} nevaljanih tokena obrisano.",
                claim.Rows.Count,
                results.Count(r => r.Status == SendStatus.Sent),
                dead.Count
            );
            return claim.Rows.Count;
        }

        // redovi čija su sva slanja dobila Retry (rezultati su redom kao pushevi)
        public static HashSet<Guid> RetryRows(IReadOnlyList<OutgoingPush> pushes, IReadOnlyList<SendResult> results)
        {
            var anyDone = new HashSet<Guid>();
            var anyRetry = new HashSet<Guid>();
            for (var i = 0; i < pushes.Count && i < results.Count; i++)
                foreach (var id in pushes[i].Notifications)
                    (results[i].Status == SendStatus.Retry ? anyRetry : anyDone).Add(id);
            anyRetry.ExceptWith(anyDone);
            return anyRetry;
        }
    }
}

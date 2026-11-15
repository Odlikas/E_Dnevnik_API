namespace E_Dnevnik_API.V2.Email
{
    // svakih ~30 s: zaključa neposlane retke iz public.email_outbox, za svaki napravi e-mail iz
    // predloška (kind + payload), pošalje kroz Resend i označi sent_at. Neuspjeh zapiše u error s
    // brojem pokušaja; red se ponavlja s odgodom (1, 4, 16, 64 min) najviše MaxAttempts puta.
    // Bez Supabasea ili RESEND_API_KEY jednom zapiše zašto ne radi i završi (aplikacija radi dalje).
    public class EmailWorker : BackgroundService
    {
        public const int BatchSize = 10;
        public const int MaxAttempts = 5;
        public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        // Resend dopušta ~2 zahtjeva u sekundi
        public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(600);

        private readonly IEmailOutboxRepository _repo;
        private readonly IEmailSender _sender;
        private readonly ILogger<EmailWorker> _logger;
        private readonly TimeProvider _time;

        // id -> ne prije; samo u memoriji (nakon restarta red odmah dobije još jedan pokušaj, broj
        // pokušaja je u bazi pa se ne ponavlja zauvijek)
        private readonly Dictionary<long, DateTimeOffset> _notBefore = new();

        public EmailWorker(IEmailOutboxRepository repo, IEmailSender sender, ILogger<EmailWorker> logger, TimeProvider time)
        {
            _repo = repo;
            _sender = sender;
            _logger = logger;
            _time = time;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_repo.IsConfigured)
            {
                _logger.LogInformation("[email] isključen: nema ConnectionStrings:Supabase.");
                return;
            }
            if (!_sender.IsConfigured)
            {
                _logger.LogWarning("[email] isključen: nema {Var}. E-mailovi čekaju u email_outbox.",
                    ResendEmailSender.ApiKeyVar);
                return;
            }
            _logger.LogInformation("[email] radi, svakih {Seconds} s.", Interval.TotalSeconds);

            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    while (await RunOnceAsync(stoppingToken) >= BatchSize && !stoppingToken.IsCancellationRequested) { }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // redovi ostaju neposlani (rollback) i idu u sljedećem krugu
                    _logger.LogWarning("[email] krug nije uspio: {Type}: {Message}", ex.GetType().Name, ex.Message);
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
            var now = _time.GetUtcNow();
            foreach (var id in _notBefore.Where(x => x.Value <= now).Select(x => x.Key).ToList())
                _notBefore.Remove(id);

            await using var claim = await _repo.ClaimAsync(EmailTemplates.KnownKinds, BatchSize, MaxAttempts,
                _notBefore.Keys.ToList(), ct);
            if (claim.Rows.Count == 0)
                return 0;

            var outcomes = new List<EmailOutcome>();
            var sent = 0;
            var stop = false;
            foreach (var row in claim.Rows)
            {
                // 429 / Resend ne radi: ostali redovi ostaju neposlani bez novog pokušaja
                if (stop)
                    break;
                var attempt = row.Attempts + 1;

                if (EmailTemplates.IsExpired(row.Payload, now))
                {
                    outcomes.Add(new EmailOutcome(row.Id, false, MaxAttempts, "link istekao, nije poslano"));
                    continue;
                }
                var email = EmailTemplates.Render(row.Kind, row.Payload);
                if (email == null)
                {
                    outcomes.Add(new EmailOutcome(row.Id, false, MaxAttempts, "payload nepotpun za " + row.Kind));
                    continue;
                }

                if (sent > 0)
                    await Task.Delay(Gap, _time, ct);
                // isti red = isti ključ: ako je e-mail otišao, a commit pao, Resend ga ne šalje dvaput
                var result = await _sender.SendAsync(row.To, email, "email_outbox/" + row.Id, ct);
                sent++;
                switch (result.Status)
                {
                    case EmailSendStatus.Sent:
                        outcomes.Add(new EmailOutcome(row.Id, true, attempt, null));
                        break;
                    case EmailSendStatus.Failed:
                        outcomes.Add(new EmailOutcome(row.Id, false, MaxAttempts, result.Error));
                        break;
                    default:
                        outcomes.Add(new EmailOutcome(row.Id, false, attempt, result.Error));
                        _notBefore[row.Id] = now + Backoff(attempt);
                        stop = result.Error?.StartsWith("resend 429") == true;
                        break;
                }
            }

            await claim.CompleteAsync(outcomes, ct);
            // bez adresa i sadržaja u logu (podaci roditelja)
            _logger.LogInformation(
                "[email] {Rows} e-mailova, {Sent} poslano, {Failed} neuspjelo.",
                claim.Rows.Count,
                outcomes.Count(o => o.Sent),
                outcomes.Count(o => !o.Sent)
            );
            foreach (var o in outcomes.Where(o => !o.Sent))
                _logger.LogWarning("[email] red {Id}, pokušaj {Attempt}: {Error}", o.Id, o.Attempts, o.Error);
            // nakon 429 čeka sljedeći krug, ne odmah sljedeću seriju
            return stop ? 0 : claim.Rows.Count;
        }

        // 1, 4, 16, 64 min
        public static TimeSpan Backoff(int attempt) => TimeSpan.FromMinutes(Math.Pow(4, Math.Clamp(attempt - 1, 0, 4)));
    }
}

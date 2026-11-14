using Microsoft.Extensions.Logging.Abstractions;

namespace E_Dnevnik_API.ScrapingServices
{
    // postavke iz konfiguracije, sekcija "Carnet" (env: Carnet__Enabled, Carnet__MaxRequestsPerSecond, ...)
    public class CarnetGuardOptions
    {
        // kill switch: false = nijedan zahtjev ne ide prema CARNET-u (promjena env + restart)
        public bool Enabled { get; set; } = true;

        // globalni limit za cijeli servis; 4/s je današnji stvarni maksimum (2 synca x 1 zahtjev / 500 ms)
        // 0 ili manje = bez limita
        public double MaxRequestsPerSecond { get; set; } = 4;

        // koliko dugo zahtjev smije čekati na svoj red; dulje = odbijen kao preopterećenje
        public TimeSpan MaxQueueWait { get; set; } = TimeSpan.FromSeconds(30);

        // circuit breaker: toliko 5xx/timeouta/mrežnih grešaka unutar prozora = pauza
        public int FailureThreshold { get; set; } = 3;
        public TimeSpan FailureWindow { get; set; } = TimeSpan.FromSeconds(60);

        // prva pauza; svaka iduća zaredom duplo dulja, najviše MaxPause
        public TimeSpan Pause { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan MaxPause { get; set; } = TimeSpan.FromHours(1);
    }

    public enum CarnetPauseReason
    {
        // kill switch (Carnet__Enabled=false)
        Disabled,

        // circuit breaker je otvoren nakon 429/5xx/timeouta
        CircuitOpen,

        // red za globalni limit je predug
        Overloaded,
    }

    // zahtjev nije poslan prema CARNET-u; 503 za v1 kontrolere, CarnetBackoff ga vidi kao preopterećenje
    public class CarnetPausedException : ScraperException
    {
        public CarnetPauseReason Reason { get; }
        public TimeSpan RetryAfter { get; }

        public CarnetPausedException(CarnetPauseReason reason, TimeSpan retryAfter)
            : base(503, "e-Dnevnik je trenutno nedostupan preko Odlikaša, pokušaj ponovo kasnije.")
        {
            Reason = reason;
            RetryAfter = retryAfter < TimeSpan.Zero ? TimeSpan.Zero : retryAfter;
        }

        // sekunde za Retry-After header, barem 1
        public int RetryAfterSeconds => Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds));
    }

    // jedno mjesto ispred svih zahtjeva prema ocjene.skole.hr: kill switch, globalni limit, circuit breaker
    // dijeli ga cijeli servis (v1 i v2), pa pauza vrijedi za sve korisnike odjednom
    public class CarnetGuard
    {
        private enum BreakerState
        {
            Closed,
            Open,
            HalfOpen,
        }

        // dok je breaker poluotvoren, ostali čekaju ishod probnog zahtjeva
        private static readonly TimeSpan HalfOpenRetryAfter = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DisabledRetryAfter = TimeSpan.FromMinutes(10);

        public static CarnetGuard Shared { get; private set; } = new(new CarnetGuardOptions());

        // poziva Program.cs jednom pri pokretanju
        public static void UseShared(CarnetGuard guard) => Shared = guard;

        private readonly object _lock = new();
        private readonly TimeProvider _time;
        private readonly CarnetRequestStats _stats;
        private readonly ILogger _logger;
        private readonly Queue<DateTimeOffset> _failures = new();

        private BreakerState _state = BreakerState.Closed;
        private DateTimeOffset _openUntil;
        private int _consecutivePauses;
        private bool _trialInFlight;
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public CarnetGuardOptions Options { get; }

        public CarnetGuard(
            CarnetGuardOptions options,
            TimeProvider? time = null,
            CarnetRequestStats? stats = null,
            ILogger? logger = null
        )
        {
            Options = options;
            _time = time ?? TimeProvider.System;
            _stats = stats ?? CarnetRequestStats.Shared;
            _logger = logger ?? NullLogger.Instance;
        }

        // HttpClient.Timeout mora pokriti čekanje u redu + sam zahtjev; guard sam javlja timeout
        public TimeSpan ClientTimeoutFor(TimeSpan requestTimeout) =>
            requestTimeout + Options.MaxQueueWait + TimeSpan.FromSeconds(10);

        // prije slanja: baca CarnetPausedException ili vraća token koji treba vratiti preko Complete
        public Permit Acquire()
        {
            if (!Options.Enabled)
            {
                _stats.RecordBlocked();
                throw new CarnetPausedException(CarnetPauseReason.Disabled, DisabledRetryAfter);
            }

            var now = _time.GetUtcNow();
            bool trial;
            lock (_lock)
            {
                trial = EnterBreaker(now);
            }

            var wait = TryReserve(now);
            if (wait is null)
            {
                if (trial)
                    ReleaseTrial();
                _stats.RecordBlocked();
                throw new CarnetPausedException(CarnetPauseReason.Overloaded, Options.MaxQueueWait);
            }
            if (wait > TimeSpan.Zero)
                _stats.RecordThrottled();
            return new Permit(wait.Value, trial);
        }

        // ishod jednog zahtjeva prema CARNET-u
        public void Complete(Permit permit, CarnetResult result, TimeSpan? retryAfter = null)
        {
            var now = _time.GetUtcNow();
            lock (_lock)
            {
                if (permit.Trial)
                    _trialInFlight = false;

                switch (result)
                {
                    case CarnetResult.Success:
                        if (permit.Trial && _state == BreakerState.HalfOpen)
                        {
                            _state = BreakerState.Closed;
                            _consecutivePauses = 0;
                            _failures.Clear();
                            _logger.LogInformation("[CARNET] breaker zatvoren, promet ide dalje.");
                        }
                        break;

                    case CarnetResult.RateLimited:
                        // 429 je izričit znak - pauza odmah, bez brojanja
                        Open(now, retryAfter, "429");
                        break;

                    case CarnetResult.Failure:
                        if (_state == BreakerState.HalfOpen && permit.Trial)
                        {
                            Open(now, retryAfter, "probni zahtjev nije uspio");
                            break;
                        }
                        _failures.Enqueue(now);
                        Prune(now);
                        if (_state == BreakerState.Closed && _failures.Count >= Options.FailureThreshold)
                            Open(now, retryAfter, $"{_failures.Count} grešaka u {Options.FailureWindow.TotalSeconds:F0} s");
                        break;

                    case CarnetResult.Abandoned:
                        // pozivatelj je odustao - ništa ne znamo o CARNET-u
                        break;
                }
            }
        }

        // null = CARNET nije pauziran
        public TimeSpan? PausedFor()
        {
            if (!Options.Enabled)
                return DisabledRetryAfter;
            lock (_lock)
            {
                var now = _time.GetUtcNow();
                return _state == BreakerState.Open && now < _openUntil ? _openUntil - now : null;
            }
        }

        // globalni red: svaki zahtjev dobije svoj termin, razmak 1/MaxRequestsPerSecond
        // null = red je predug
        public TimeSpan? TryReserve(DateTimeOffset now)
        {
            if (Options.MaxRequestsPerSecond <= 0)
                return TimeSpan.Zero;
            var interval = TimeSpan.FromSeconds(1 / Options.MaxRequestsPerSecond);
            lock (_lock)
            {
                var slot = _nextSlot > now ? _nextSlot : now;
                var wait = slot - now;
                if (wait > Options.MaxQueueWait)
                    return null;
                _nextSlot = slot + interval;
                return wait;
            }
        }

        // pod lockom; true = ovaj zahtjev je probni (half-open)
        private bool EnterBreaker(DateTimeOffset now)
        {
            if (_state == BreakerState.Open)
            {
                if (now < _openUntil)
                {
                    _stats.RecordBlocked();
                    throw new CarnetPausedException(CarnetPauseReason.CircuitOpen, _openUntil - now);
                }
                _state = BreakerState.HalfOpen;
            }

            if (_state == BreakerState.HalfOpen)
            {
                if (_trialInFlight)
                {
                    _stats.RecordBlocked();
                    throw new CarnetPausedException(CarnetPauseReason.CircuitOpen, HalfOpenRetryAfter);
                }
                _trialInFlight = true;
                return true;
            }
            return false;
        }

        private void ReleaseTrial()
        {
            lock (_lock)
                _trialInFlight = false;
        }

        // pod lockom
        private void Open(DateTimeOffset now, TimeSpan? retryAfter, string why)
        {
            var pause = TimeSpan.FromTicks(
                Options.Pause.Ticks * (1L << Math.Min(_consecutivePauses, 10))
            );
            if (retryAfter is { } ra && ra > pause)
                pause = ra;
            if (pause > Options.MaxPause)
                pause = Options.MaxPause;

            _state = BreakerState.Open;
            _openUntil = now + pause;
            _consecutivePauses++;
            _failures.Clear();
            _stats.RecordBreakerOpen();
            _logger.LogWarning(
                "[CARNET] breaker otvoren ({Why}): pauza {Minutes:F1} min, do {Until:u}",
                why,
                pause.TotalMinutes,
                _openUntil
            );
        }

        private void Prune(DateTimeOffset now)
        {
            while (_failures.Count > 0 && now - _failures.Peek() > Options.FailureWindow)
                _failures.Dequeue();
        }

        public readonly record struct Permit(TimeSpan Wait, bool Trial);
    }

    public enum CarnetResult
    {
        Success,
        RateLimited,
        Failure,
        Abandoned,
    }

    // handler u lancu CarnetHttp: guard -> brojanje -> mreža
    // blokirani zahtjevi ne dođu do brojanja, pa statistika broji samo ono što je stvarno otišlo
    public class CarnetGuardHandler : DelegatingHandler
    {
        private readonly CarnetGuard _guard;
        private readonly TimeProvider _time;
        private readonly TimeSpan _timeout;

        public CarnetGuardHandler(CarnetGuard guard, TimeSpan timeout, TimeProvider? time = null)
        {
            _guard = guard;
            _timeout = timeout;
            _time = time ?? TimeProvider.System;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var permit = _guard.Acquire();
            var result = CarnetResult.Abandoned;
            TimeSpan? retryAfter = null;
            try
            {
                if (permit.Wait > TimeSpan.Zero)
                    await Task.Delay(permit.Wait, _time, cancellationToken);

                // vlastiti timeout: samo tako razlikujemo spori CARNET od korisnika koji je odustao
                using var timeoutCts = new CancellationTokenSource(_timeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeoutCts.Token
                );

                HttpResponseMessage response;
                try
                {
                    response = await base.SendAsync(request, linked.Token);
                    try
                    {
                        // tijelo skidamo ovdje, pod istim timeoutom (CARNET stranice su mali HTML)
                        await response.Content.LoadIntoBufferAsync().WaitAsync(linked.Token);
                    }
                    catch
                    {
                        response.Dispose();
                        throw;
                    }
                }
                catch (OperationCanceledException) when (
                    timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                )
                {
                    result = CarnetResult.Failure;
                    throw new TaskCanceledException(
                        $"CARNET request timed out after {_timeout.TotalSeconds:F0} s.",
                        new TimeoutException()
                    );
                }
                catch (HttpRequestException)
                {
                    // mreža / DNS / TLS
                    result = CarnetResult.Failure;
                    throw;
                }

                var status = (int)response.StatusCode;
                if (status == 429)
                {
                    result = CarnetResult.RateLimited;
                    retryAfter = RetryAfter(response);
                }
                else if (status >= 500)
                {
                    result = CarnetResult.Failure;
                    retryAfter = RetryAfter(response);
                }
                else
                {
                    result = CarnetResult.Success;
                }
                return response;
            }
            finally
            {
                _guard.Complete(permit, result, retryAfter);
            }
        }

        private TimeSpan? RetryAfter(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header?.Delta is { } delta)
                return delta;
            if (header?.Date is { } date)
                return date - _time.GetUtcNow();
            return null;
        }
    }
}

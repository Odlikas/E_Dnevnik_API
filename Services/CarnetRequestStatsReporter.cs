using E_Dnevnik_API.ScrapingServices;

namespace E_Dnevnik_API.Services
{
    // jednom dnevno (ponoć po Europe/Zagreb) logira koliko smo zahtjeva poslali CARNET-u
    // brojači su samo u memoriji - pri gašenju aplikacije logiramo djelomični sažetak da se ne izgubi
    public class CarnetRequestStatsReporter : BackgroundService
    {
        private readonly CarnetRequestStats _stats;
        private readonly ILogger<CarnetRequestStatsReporter> _logger;
        private readonly TimeZoneInfo _zone;
        private DateTime _periodStartUtc = DateTime.UtcNow;

        public CarnetRequestStatsReporter(
            CarnetRequestStats stats,
            ILogger<CarnetRequestStatsReporter> logger
        )
        {
            _stats = stats;
            _logger = logger;
            _zone = ResolveZagreb();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = UntilNextMidnight(DateTime.UtcNow, _zone);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
                LogSummary("dnevni");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await base.StopAsync(cancellationToken);
            LogSummary("djelomični (gašenje)");
        }

        private void LogSummary(string kind)
        {
            var s = _stats.SnapshotAndReset();
            var periodStart = _periodStartUtc;
            _periodStartUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "[CARNET] {Kind} sažetak od {PeriodStart:u} do {PeriodEnd:u}: ukupno {Total} zahtjeva "
                    + "(login {Login}, korisnički {UserScrape}, pozadinski {Background}), "
                    + "cache hitova {CacheHits}, korisnika {UniqueUsers}, zahtjeva po korisniku {PerUser:F1}",
                kind,
                periodStart,
                _periodStartUtc,
                s.Total,
                s.Login,
                s.UserScrape,
                s.Background,
                s.CacheHits,
                s.UniqueUsers,
                s.RequestsPerUser
            );
        }

        public static TimeSpan UntilNextMidnight(DateTime utcNow, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
            var nextMidnightLocal = DateTime.SpecifyKind(local.Date.AddDays(1), DateTimeKind.Unspecified);
            var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightLocal, zone);
            return nextMidnightUtc - utcNow;
        }

        private static TimeZoneInfo ResolveZagreb()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");
            }
            catch (Exception)
            {
                // ako kontejner nema tzdata, sažetak ide u ponoć UTC
                return TimeZoneInfo.Utc;
            }
        }
    }
}

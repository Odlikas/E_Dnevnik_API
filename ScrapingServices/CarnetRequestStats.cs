using System.Security.Cryptography;
using System.Text;

namespace E_Dnevnik_API.ScrapingServices
{
    public record CarnetRequestSnapshot(
        long Login,
        long UserScrape,
        long Background,
        long CacheHits,
        int UniqueUsers,
        // CarnetGuard: odbijeni (kill switch / breaker / predug red), zadržani u redu, otvaranja breakera
        long Blocked = 0,
        long Throttled = 0,
        long BreakerOpens = 0
    )
    {
        public long Total => Login + UserScrape + Background;

        public double RequestsPerUser => UniqueUsers == 0 ? 0 : (double)Total / UniqueUsers;
    }

    // brojači odlaznih zahtjeva prema ocjene.skole.hr, samo u memoriji
    // bez PII: korisnike pamtimo kao hash emaila samo da ih prebrojimo, van izlaze samo brojevi
    public class CarnetRequestStats
    {
        public static CarnetRequestStats Shared { get; } = new();

        private readonly object _lock = new();
        private long _login,
            _userScrape,
            _background,
            _cacheHits,
            _blocked,
            _throttled,
            _breakerOpens;
        private HashSet<string> _users = new();

        public void RecordBlocked()
        {
            lock (_lock)
                _blocked++;
        }

        public void RecordThrottled()
        {
            lock (_lock)
                _throttled++;
        }

        public void RecordBreakerOpen()
        {
            lock (_lock)
                _breakerOpens++;
        }

        public void RecordRequest(CarnetRequestSource source, string? userKey)
        {
            lock (_lock)
            {
                switch (source)
                {
                    case CarnetRequestSource.Login:
                        _login++;
                        break;
                    case CarnetRequestSource.UserScrape:
                        _userScrape++;
                        break;
                    case CarnetRequestSource.Background:
                        _background++;
                        break;
                }

                if (!string.IsNullOrEmpty(userKey))
                    _users.Add(Hash(userKey));
            }
        }

        // odgovor poslužen iz cachea - prema CARNET-u nije otišao nijedan zahtjev
        public void RecordCacheHit()
        {
            lock (_lock)
                _cacheHits++;
        }

        public CarnetRequestSnapshot Snapshot()
        {
            lock (_lock)
                return new CarnetRequestSnapshot(
                    _login,
                    _userScrape,
                    _background,
                    _cacheHits,
                    _users.Count,
                    _blocked,
                    _throttled,
                    _breakerOpens
                );
        }

        // vraća stanje i kreće brojati ispočetka (za dnevni sažetak)
        public CarnetRequestSnapshot SnapshotAndReset()
        {
            lock (_lock)
            {
                var snapshot = Snapshot();
                _login = _userScrape = _background = _cacheHits = 0;
                _blocked = _throttled = _breakerOpens = 0;
                _users = new HashSet<string>();
                return snapshot;
            }
        }

        private static string Hash(string userKey) =>
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(userKey.Trim().ToLowerInvariant()))
            );
    }
}

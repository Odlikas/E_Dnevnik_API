using System.Security.Cryptography;
using System.Text;

namespace E_Dnevnik_API.ScrapingServices
{
    public record CarnetRequestSnapshot(
        long Login,
        long UserScrape,
        long Background,
        long CacheHits,
        int UniqueUsers
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
            _cacheHits;
        private HashSet<string> _users = new();

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
                    _users.Count
                );
        }

        // vraća stanje i kreće brojati ispočetka (za dnevni sažetak)
        public CarnetRequestSnapshot SnapshotAndReset()
        {
            lock (_lock)
            {
                var snapshot = Snapshot();
                _login = _userScrape = _background = _cacheHits = 0;
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

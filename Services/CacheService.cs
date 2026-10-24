using E_Dnevnik_API.Database;
using E_Dnevnik_API.Database.Models;
using E_Dnevnik_API.ScrapingServices;
using Newtonsoft.Json;

namespace E_Dnevnik_API.Services
{
    public class CacheService
    {
        private readonly AppDbContext _db;

        public static readonly TimeSpan ProfileTTL = TimeSpan.FromDays(7);
        public static readonly TimeSpan ScheduleTTL = TimeSpan.FromDays(7);
        public static readonly TimeSpan GradesTTL = TimeSpan.FromHours(2);
        public static readonly TimeSpan TestsTTL = TimeSpan.FromHours(1);
        public static readonly TimeSpan AbsencesTTL = TimeSpan.FromHours(1);
        public static readonly TimeSpan GradesDifferentTTL = TimeSpan.FromHours(24);
        private static readonly TimeSpan ForceRefreshCooldown = TimeSpan.FromMinutes(15);

        public CacheService(AppDbContext db)
        {
            _db = db;
        }

        // Generic cache-or-fetch. Returns (data, cachedAt, isFromCache).
        // Uses Newtonsoft.Json — required because several models have non-default constructors.
        public async Task<(T data, DateTime cachedAt, bool fromCache)> GetOrFetch<T>(
            string email,
            Func<StudentCache, string?> getData,
            Func<StudentCache, DateTime?> getCachedAt,
            Action<StudentCache, string, DateTime> setData,
            TimeSpan ttl,
            Func<Task<T>> fetchFromCarnet,
            bool forceRefresh = false)
        {
            var cache = await _db.StudentCache.FindAsync(email);
            if (cache == null)
            {
                cache = new StudentCache { Email = email };
                _db.StudentCache.Add(cache);
            }

            var cachedAt = getCachedAt(cache);
            var isFresh = cachedAt != null && cachedAt.Value > DateTime.UtcNow - ttl;

            // Enforce force-refresh cooldown — prevents hammering CARNET on pull-to-refresh
            if (forceRefresh &&
                cache.LastForceRefreshAt != null &&
                cache.LastForceRefreshAt > DateTime.UtcNow - ForceRefreshCooldown)
            {
                forceRefresh = false;
            }

            if (!forceRefresh && isFresh && getData(cache) != null)
            {
                cache.LastActiveAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                CarnetRequestStats.Shared.RecordCacheHit();
                return (JsonConvert.DeserializeObject<T>(getData(cache)!)!, cachedAt!.Value, true);
            }

            // Cache miss or approved force refresh
            var data = await fetchFromCarnet();
            var now = DateTime.UtcNow;
            setData(cache, JsonConvert.SerializeObject(data), now);
            cache.LastActiveAt = now;
            if (forceRefresh) cache.LastForceRefreshAt = now;
            await _db.SaveChangesAsync();

            return (data, now, false);
        }

        public async Task<StudentCache?> GetRawCache(string email)
            => await _db.StudentCache.FindAsync(email);
    }
}

using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Push
{
    // public.device_sync_signals u Supabaseu, kao uloga ednevnik_service (migracija 20261113000300)
    public class SyncSignalRepository : ISyncSignalRepository
    {
        // dospjeli redovi (novi ili promijenjeni nakon zadnjeg slanja, ne stariji od 10 min), odmah
        // označeni; drugi radnik zaključane preskače
        public const string TakeDueSql =
            "with due as ("
            + "select device_id from public.device_sync_signals "
            + "where (sent_at is null or sent_at < requested_at) and requested_at > now() - interval '10 minutes' "
            + "order by requested_at limit $1 for update skip locked) "
            + "update public.device_sync_signals s set sent_at = now() "
            + "from due join public.devices d on d.id = due.device_id "
            + "where s.device_id = due.device_id "
            + "returning s.device_id, s.reason, d.push_token, d.revoked_at is not null";

        private readonly NpgsqlDataSource? _db;

        public SyncSignalRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        public async Task<IReadOnlyList<SyncSignal>> TakeDueAsync(int limit, CancellationToken ct)
        {
            var db = _db ?? throw new InvalidOperationException("Supabase DB not configured.");
            await using var cmd = db.CreateCommand(TakeDueSql);
            cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
            var rows = new List<SyncSignal>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                rows.Add(new SyncSignal(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetBoolean(3)));
            return rows;
        }

        public async Task ClearDeviceTokensAsync(IReadOnlyCollection<string> tokens, CancellationToken ct)
        {
            var db = _db ?? throw new InvalidOperationException("Supabase DB not configured.");
            await using var cmd = db.CreateCommand(PushRepository.ClearDeviceTokensSql);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                Value = tokens.Distinct().ToArray(),
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            });
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}

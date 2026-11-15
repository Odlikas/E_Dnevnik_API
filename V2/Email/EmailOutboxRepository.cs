using System.Text.Json.Nodes;
using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Email
{
    // public.email_outbox u Supabaseu, kao uloga ednevnik_service (select + update sent_at, error;
    // migracija 20261115000503_parent_consent u aplikaciji)
    public class EmailOutboxRepository : IEmailOutboxRepository
    {
        // broj pokušaja je na početku stupca error ("3|poruka"); error bez broja (napisan rukom) = 1
        public const string AttemptsSql =
            "case when o.error is null then 0 "
            + "else coalesce(substring(o.error from '^([0-9]{1,3})\\|')::int, 1) end";

        // neposlani redovi zadnjih 7 dana (toliko vrijede linkovi); zaključani dok se ne pošalju,
        // drugi radnik ih preskače
        public const string ClaimSql =
            "select o.id, o.kind, o.to_email, o.payload::text, a.attempts, o.created_at "
            + "from public.email_outbox o "
            + "cross join lateral (select " + AttemptsSql + " as attempts) a "
            + "where o.sent_at is null and o.created_at > now() - interval '7 days' "
            + "and o.kind = any($1) and a.attempts < $3 and not (o.id = any($4)) "
            + "order by o.created_at "
            + "limit $2 "
            + "for update of o skip locked";

        public const string MarkSentSql = "update public.email_outbox set sent_at = now(), error = null where id = $1";
        public const string MarkErrorSql = "update public.email_outbox set error = $2 where id = $1";

        // dovoljno za poruku Resenda, a da stupac ne raste
        public const int MaxErrorLength = 500;

        private readonly NpgsqlDataSource? _db;

        public EmailOutboxRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        public async Task<IEmailClaim> ClaimAsync(
            IReadOnlyCollection<string> kinds,
            int limit,
            int maxAttempts,
            IReadOnlyCollection<long> skip,
            CancellationToken ct
        )
        {
            var db = _db ?? throw new InvalidOperationException("Supabase DB not configured.");
            var conn = await db.OpenConnectionAsync(ct);
            NpgsqlTransaction? tx = null;
            try
            {
                tx = await conn.BeginTransactionAsync(ct);
                var rows = new List<PendingEmail>();
                await using (var cmd = new NpgsqlCommand(ClaimSql, conn, tx))
                {
                    cmd.Parameters.Add(new NpgsqlParameter
                    {
                        Value = kinds.ToArray(),
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                    });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = maxAttempts });
                    cmd.Parameters.Add(new NpgsqlParameter
                    {
                        Value = skip.Distinct().ToArray(),
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                    });
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        rows.Add(new PendingEmail(
                            r.GetInt64(0),
                            r.GetString(1),
                            r.GetString(2),
                            ParsePayload(r.GetString(3)),
                            r.GetInt32(4),
                            r.GetFieldValue<DateTimeOffset>(5)
                        ));
                }
                return new Claim(conn, tx, rows);
            }
            catch
            {
                if (tx != null)
                    await tx.DisposeAsync();
                await conn.DisposeAsync();
                throw;
            }
        }

        // payload je jsonb objekt; sve drugo (null, polje) = prazan objekt, pa predložak vrati null
        public static JsonObject ParsePayload(string json)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException)
            {
                return new JsonObject();
            }
        }

        // "3|poruka", skraćeno
        public static string ErrorText(int attempts, string error)
        {
            var text = attempts + "|" + error.Replace('\n', ' ').Replace('\r', ' ');
            return text.Length <= MaxErrorLength ? text : text[..MaxErrorLength];
        }

        private sealed class Claim : IEmailClaim
        {
            private readonly NpgsqlConnection _conn;
            private readonly NpgsqlTransaction _tx;
            private bool _done;

            public Claim(NpgsqlConnection conn, NpgsqlTransaction tx, List<PendingEmail> rows)
            {
                _conn = conn;
                _tx = tx;
                Rows = rows;
            }

            public IReadOnlyList<PendingEmail> Rows { get; }

            public async Task CompleteAsync(IReadOnlyCollection<EmailOutcome> outcomes, CancellationToken ct)
            {
                foreach (var o in outcomes)
                {
                    await using var cmd = new NpgsqlCommand(o.Sent ? MarkSentSql : MarkErrorSql, _conn, _tx);
                    cmd.Parameters.Add(new NpgsqlParameter { Value = o.Id });
                    if (!o.Sent)
                        cmd.Parameters.Add(new NpgsqlParameter { Value = ErrorText(o.Attempts, o.Error ?? "nepoznata greška") });
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                await _tx.CommitAsync(ct);
                _done = true;
            }

            public async ValueTask DisposeAsync()
            {
                if (!_done)
                {
                    try
                    {
                        await _tx.RollbackAsync();
                    }
                    catch (Exception)
                    {
                        // veza je možda već pala; zaključavanje ionako nestaje s njom
                    }
                }
                await _tx.DisposeAsync();
                await _conn.DisposeAsync();
            }
        }
    }
}

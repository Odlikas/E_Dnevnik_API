using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Push
{
    // red obavijesti u Supabaseu, kao uloga ednevnik_service (grantovi: migracija 0900 push_sender)
    public class PushRepository : IPushRepository
    {
        // neposlani redovi zadnja 24 h; zaključani dok se ne pošalju, drugi radnik ih preskače
        public const string ClaimSql =
            "select n.id, n.family_id, n.parent_id, n.child_id, n.type, c.first_name, s.name_hr, "
            // D314: every cast is guarded, so one malformed row can't fail the whole claim
            + "coalesce(g.value, case when n.payload ->> 'value' ~ '^[0-9]{1,4}$' then (n.payload ->> 'value')::smallint end), "
            + "g.note, g.grading_element, "
            + "case when n.payload ->> 'request_id' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' "
            + "then (n.payload ->> 'request_id')::uuid end "
            + "from public.notifications n "
            + "left join public.children c on c.id = n.child_id "
            + "left join public.grades g on g.id = case when n.payload ->> 'grade_id' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' "
            + "then (n.payload ->> 'grade_id')::uuid end "
            + "left join public.subjects s on s.id = case when n.payload ->> 'subject_id' ~ '^[0-9]{1,4}$' "
            + "then (n.payload ->> 'subject_id')::smallint end "
            + "where n.pushed_at is null and n.created_at > now() - interval '24 hours' and n.type = any($1) "
            + "order by n.created_at "
            + "limit $2 "
            + "for update of n skip locked";

        public const string RecipientsSql =
            "select 'parent', p.id, p.family_id, null::uuid, p.push_token from public.parents p "
            + "where p.family_id = any($1) and p.push_token is not null and p.push_token <> '' "
            + "union all "
            + "select 'device', d.id, d.family_id, d.child_id, d.push_token from public.devices d "
            + "where d.child_id = any($2) and d.revoked_at is null and d.push_token is not null and d.push_token <> ''";

        public const string MarkPushedSql = "update public.notifications set pushed_at = now() where id = any($1)";

        // samo taj token: ako je mobitel u međuvremenu spremio novi, ostaje
        public const string ClearParentTokensSql =
            "update public.parents set push_token = null where push_token = any($1)";
        public const string ClearDeviceTokensSql =
            "update public.devices set push_token = null where push_token = any($1)";

        private readonly NpgsqlDataSource? _db;

        public PushRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        public async Task<IPushClaim> ClaimAsync(IReadOnlyCollection<string> types, int limit, CancellationToken ct)
        {
            var db = _db ?? throw new InvalidOperationException("Supabase DB not configured.");
            var conn = await db.OpenConnectionAsync(ct);
            NpgsqlTransaction? tx = null;
            try
            {
                tx = await conn.BeginTransactionAsync(ct);
                var rows = new List<PendingPush>();
                await using (var cmd = new NpgsqlCommand(ClaimSql, conn, tx))
                {
                    cmd.Parameters.Add(new NpgsqlParameter
                    {
                        Value = types.ToArray(),
                        NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                    });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        rows.Add(new PendingPush(
                            r.GetGuid(0),
                            r.GetGuid(1),
                            r.IsDBNull(2) ? null : r.GetGuid(2),
                            r.IsDBNull(3) ? null : r.GetGuid(3),
                            r.GetString(4),
                            r.IsDBNull(5) ? null : r.GetString(5),
                            r.IsDBNull(6) ? null : r.GetString(6),
                            r.IsDBNull(7) ? null : r.GetInt16(7),
                            r.IsDBNull(8) ? null : r.GetString(8),
                            r.IsDBNull(9) ? null : r.GetString(9),
                            r.IsDBNull(10) ? null : r.GetGuid(10)
                        ));
                }

                var recipients = new List<PushRecipient>();
                if (rows.Count > 0)
                {
                    await using var cmd = new NpgsqlCommand(RecipientsSql, conn, tx);
                    cmd.Parameters.Add(UuidArray(rows.Select(x => x.FamilyId)));
                    cmd.Parameters.Add(UuidArray(rows.Where(x => x.ChildId != null).Select(x => x.ChildId!.Value)));
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                        recipients.Add(new PushRecipient(
                            r.GetString(0) == "parent" ? RecipientKind.Parent : RecipientKind.Device,
                            r.GetGuid(1),
                            r.GetGuid(2),
                            r.IsDBNull(3) ? null : r.GetGuid(3),
                            r.GetString(4)
                        ));
                }
                return new Claim(conn, tx, rows, recipients);
            }
            catch
            {
                if (tx != null)
                    await tx.DisposeAsync();
                await conn.DisposeAsync();
                throw;
            }
        }

        private static NpgsqlParameter UuidArray(IEnumerable<Guid> ids) =>
            new() { Value = ids.Distinct().ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };

        private static NpgsqlParameter TextArray(IEnumerable<string> values) =>
            new() { Value = values.Distinct().ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

        private sealed class Claim : IPushClaim
        {
            private readonly NpgsqlConnection _conn;
            private readonly NpgsqlTransaction _tx;
            private bool _done;

            public Claim(NpgsqlConnection conn, NpgsqlTransaction tx, List<PendingPush> rows, List<PushRecipient> recipients)
            {
                _conn = conn;
                _tx = tx;
                Rows = rows;
                Recipients = recipients;
            }

            public IReadOnlyList<PendingPush> Rows { get; }
            public IReadOnlyList<PushRecipient> Recipients { get; }

            public async Task CompleteAsync(
                IReadOnlyCollection<Guid> pushed,
                IReadOnlyCollection<string> deadTokens,
                CancellationToken ct
            )
            {
                if (pushed.Count > 0)
                {
                    await using var cmd = new NpgsqlCommand(MarkPushedSql, _conn, _tx);
                    cmd.Parameters.Add(UuidArray(pushed));
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                if (deadTokens.Count > 0)
                {
                    foreach (var sql in new[] { ClearParentTokensSql, ClearDeviceTokensSql })
                    {
                        await using var cmd = new NpgsqlCommand(sql, _conn, _tx);
                        cmd.Parameters.Add(TextArray(deadTokens));
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
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

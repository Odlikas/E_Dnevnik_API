using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Ai
{
    public interface IAiRepository
    {
        bool IsConfigured { get; }
        Task<Asker?> GetAskerAsync(Guid authUserId, CancellationToken ct);
        Task<int> CountQuestionsAsync(Guid childId, DateTimeOffset since, CancellationToken ct);

        // zadnjih [limit] poruka od [since], najstarija prva (kontekst za model)
        Task<List<ChatTurn>> RecentAsync(Guid childId, DateTimeOffset since, int limit, CancellationToken ct);
        Task SaveAsync(Asker asker, string question, AskMessage answer, CancellationToken ct);
        Task LogCallAsync(AiCall call, CancellationToken ct);

        // roditeljeva pravila za slikanje; Default kad redak ne postoji
        Task<PhotoHelpRules> GetPhotoRulesAsync(Guid childId, CancellationToken ct);
        Task<int> CountPhotosAsync(Guid childId, DateTimeOffset since, CancellationToken ct);
        Task<Guid> SavePhotoAsync(Asker asker, string subject, PhotoResult result, CancellationToken ct);
    }

    // jedan redak public.ai_calls; Feature 'ask' | 'photo'
    public record AiCall(Guid FamilyId, string Feature, string Provider, bool Ok, int Images, int InputTokens, int OutputTokens, int LatencyMs);

    // čisti Npgsql kao uloga ednevnik_service (grantovi u 20261118000100_ask_messages.sql)
    public class AiRepository : IAiRepository
    {
        private readonly NpgsqlDataSource? _db;

        public AiRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Supabase is not configured");

        public async Task<Asker?> GetAskerAsync(Guid authUserId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select c.id, c.family_id, c.grade, c.school_level::text, f.plan::text, coalesce(c.ai_allowed, false)
                from public.children c join public.families f on f.id = c.family_id
                where c.auth_user_id = $1 and c.deleted_at is null
                limit 1
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = authUserId });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            return new Asker(
                r.GetGuid(0),
                r.GetGuid(1),
                r.IsDBNull(2) ? null : r.GetInt16(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? "free" : r.GetString(4),
                r.GetBoolean(5)
            );
        }

        public async Task<int> CountQuestionsAsync(Guid childId, DateTimeOffset since, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select count(*) from public.ask_messages where child_id = $1 and from_child and created_at >= $2"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = since.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        public async Task<List<ChatTurn>> RecentAsync(Guid childId, DateTimeOffset since, int limit, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select from_child, body, steps from public.ask_messages
                where child_id = $1 and created_at >= $2
                order by created_at desc, id desc limit $3
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = since.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = limit });
            var turns = new List<ChatTurn>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var steps = r.GetFieldValue<string[]>(2);
                var text = steps.Length == 0 ? r.GetString(1) : r.GetString(1) + "\n" + string.Join("\n", steps);
                turns.Add(new ChatTurn(r.GetBoolean(0), text));
            }
            turns.Reverse();
            return turns;
        }

        public async Task SaveAsync(Asker asker, string question, AskMessage answer, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                insert into public.ask_messages (child_id, family_id, from_child, body, steps, created_at)
                values ($1, $2, true, $3, '{}', now()), ($1, $2, false, $4, $5, now() + interval '1 millisecond')
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.ChildId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.FamilyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = question });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = answer.Text });
            cmd.Parameters.Add(
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = answer.Steps.ToArray() }
            );
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task LogCallAsync(AiCall call, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                insert into public.ai_calls (family_id, feature, route, provider, images, input_tokens, output_tokens, ok, latency_ms)
                values ($1, $2, 'small', $3, $4, $5, $6, $7, $8)
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = call.FamilyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = call.Feature });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = call.Provider });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)call.Images });
            cmd.Parameters.Add(new NpgsqlParameter { Value = call.InputTokens });
            cmd.Parameters.Add(new NpgsqlParameter { Value = call.OutputTokens });
            cmd.Parameters.Add(new NpgsqlParameter { Value = call.Ok });
            cmd.Parameters.Add(new NpgsqlParameter { Value = call.LatencyMs });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        public async Task<PhotoHelpRules> GetPhotoRulesAsync(Guid childId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select photo_help_enabled, photo_help_level, photo_help_daily_cap, photo_help_subjects
                from public.child_settings where child_id = $1
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return PhotoHelpRules.Default;
            return new PhotoHelpRules(
                r.IsDBNull(0) || r.GetBoolean(0),
                r.IsDBNull(1) ? "steps" : r.GetString(1),
                r.IsDBNull(2) ? PhotoHelpRules.Default.DailyCap : r.GetInt16(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<short[]>(3)
            );
        }

        public async Task<int> CountPhotosAsync(Guid childId, DateTimeOffset since, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select count(*) from public.photo_explanations where child_id = $1 and created_at >= $2"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = since.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        public async Task<Guid> SavePhotoAsync(Asker asker, string subject, PhotoResult result, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                insert into public.photo_explanations
                  (child_id, family_id, subject, expression, topic, answer, steps, similar_tasks)
                values ($1, $2, $3, $4, $5, $6, $7, $8)
                returning id
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.ChildId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.FamilyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = subject });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.Expression });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.Topic });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.Answer });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = result.Steps.ToJsonString() });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = result.SimilarTasks.ToJsonString() });
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
    }
}

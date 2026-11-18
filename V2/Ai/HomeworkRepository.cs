using System.Text.Json.Nodes;
using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Ai
{
    public interface IHomeworkRepository
    {
        bool IsConfigured { get; }

        // provjere zadaće djeteta od [since]
        Task<int> CountChecksAsync(Guid childId, DateTimeOffset since, CancellationToken ct);
        Task<Guid> SaveCheckAsync(Asker asker, HomeworkResult result, CancellationToken ct);
    }

    // čisti Npgsql kao uloga ednevnik_service (grantovi u 20261118000600_homework_ai_check.sql)
    public class HomeworkRepository : IHomeworkRepository
    {
        private readonly NpgsqlDataSource? _db;

        public HomeworkRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Supabase is not configured");

        public async Task<int> CountChecksAsync(Guid childId, DateTimeOffset since, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select count(*) from public.homework_checks where child_id = $1 and created_at >= $2"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = since.UtcDateTime });
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        public async Task<Guid> SaveCheckAsync(Asker asker, HomeworkResult result, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                insert into public.homework_checks
                  (child_id, family_id, items, answers, status, path, subject, title)
                values ($1, $2, $3, $4, $5::public.hw_status, 'ai_big', $6, $7)
                returning id
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.ChildId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = asker.FamilyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = ItemsOf(result).ToJsonString() });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = AnswersOf(result).ToJsonString() });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.HasMistakes ? "has_mistakes" : "passed" });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.Subject });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = result.Title });
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // homework_checks.items: retci bez rješenja
        public static JsonArray ItemsOf(HomeworkResult result)
        {
            var items = new JsonArray();
            foreach (var l in result.Lines)
            {
                var item = new JsonObject { ["number"] = l.Number, ["text"] = l.Text, ["correct"] = l.Correct };
                if (!l.Correct)
                {
                    item["prompt"] = l.Prompt ?? "";
                    item["answer_hint"] = l.AnswerHint ?? "";
                    item["hints"] = new JsonArray(l.Hints.Select(h => (JsonNode?)h).ToArray());
                }
                items.Add(item);
            }
            return items;
        }

        // homework_checks.answers: {"<broj>": {"answer", "accepted"}} za pogrešne retke
        public static JsonObject AnswersOf(HomeworkResult result)
        {
            var answers = new JsonObject();
            foreach (var l in result.Lines.Where(l => !l.Correct && l.Answer is not null))
                answers[l.Number.ToString()] = new JsonObject
                {
                    ["answer"] = l.Answer,
                    ["accepted"] = new JsonArray(l.Accepted.Select(a => (JsonNode?)a).ToArray()),
                };
            return answers;
        }
    }
}

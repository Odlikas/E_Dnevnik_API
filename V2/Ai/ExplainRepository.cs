using System.Text.Json.Nodes;
using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Ai
{
    public interface IExplainRepository
    {
        bool IsConfigured { get; }

        // null kad pozivatelj nije roditelj iz obitelji tog zahtjeva za pomoć
        Task<HelpContext?> GetContextAsync(Guid parentId, Guid notificationId, CancellationToken ct);
        Task<ExistingNote?> FindNoteAsync(Guid familyId, string source, CancellationToken ct);

        // objašnjenja koja je obitelj dobila od [since] (i obrisana broje)
        Task<int> CountExplanationsAsync(Guid familyId, DateTimeOffset since, CancellationToken ct);
        Task<Guid> SaveNoteAsync(HelpContext help, ExplainNote note, string detail, CancellationToken ct);
    }

    // čisti Npgsql kao uloga ednevnik_service (grantovi u 20261118000300_help_explanations.sql)
    public class ExplainRepository : IExplainRepository
    {
        // djetetovo pitanje Josi najviše toliko prije zahtjeva za pomoć je kontekst
        private static readonly TimeSpan QuestionWindow = TimeSpan.FromHours(3);

        private readonly NpgsqlDataSource? _db;

        public ExplainRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Supabase is not configured");

        public async Task<HelpContext?> GetContextAsync(Guid parentId, Guid notificationId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select n.child_id, n.family_id, c.first_name, c.grade, c.school_level::text, f.plan::text,
                       n.payload->>'topic',
                       (select q.body from public.ask_messages q
                         where q.child_id = n.child_id and q.from_child
                           and q.created_at between n.created_at - $3 and n.created_at + interval '1 minute'
                         order by q.created_at desc limit 1)
                from public.notifications n
                join public.parents p on p.family_id = n.family_id and p.id = $1
                join public.children c on c.id = n.child_id and c.deleted_at is null
                join public.families f on f.id = n.family_id
                where n.id = $2 and n.type = 'help_request'
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = parentId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = notificationId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = QuestionWindow });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            return new HelpContext(
                notificationId,
                r.GetGuid(0),
                r.GetGuid(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt16(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? "free" : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7)
            );
        }

        public async Task<ExistingNote?> FindNoteAsync(Guid familyId, string source, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select id, title, jsonb_array_length(coalesce(body->'sections', '[]'::jsonb))
                from public.study_notes
                where family_id = $1 and body->>'source' = $2 and deleted_at is null
                order by created_at desc limit 1
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = familyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = source });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            return new ExistingNote(r.GetGuid(0), r.GetString(1), r.GetInt32(2));
        }

        public async Task<int> CountExplanationsAsync(Guid familyId, DateTimeOffset since, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                """
                select count(*) from public.study_notes
                where family_id = $1 and body->>'source' like 'help:%' and created_at >= $2
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = familyId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = since });
            return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<Guid> SaveNoteAsync(HelpContext help, ExplainNote note, string detail, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["kind"] = "explanation",
                ["source"] = help.Source,
                ["topic"] = note.Topic,
                ["detail"] = detail,
                ["sections"] = note.Sections.DeepClone(),
            };
            if (note.JosoSays is not null)
                body["joso_says"] = note.JosoSays;
            await using var cmd = Db.CreateCommand(
                """
                insert into public.study_notes (child_id, family_id, title, subject_id, body)
                values ($1, $2, $3, $4, $5)
                returning id
                """
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = help.ChildId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = help.FamilyId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = note.Title });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = note.SubjectId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = body.ToJsonString() });
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }
    }
}

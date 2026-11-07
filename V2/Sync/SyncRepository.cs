using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Sync
{
    public record ChildRecord(Guid Id, Guid FamilyId, Guid? AuthUserId, bool SelfManaged);

    public record ChildSubjectRow(short SubjectId, string? EdnevnikRef, string? TeacherName);

    public record CalendarTestRow(short? SubjectId, string Title, DateOnly EventDate);

    // sve što jedan sync piše u području D, u jednoj transakciji
    public record SyncWrite(
        Guid RunId,
        Guid ChildId,
        Guid FamilyId,
        SchoolYear Year,
        List<ChildSubjectRow> ChildSubjects,
        List<GradeRow> Grades,
        List<CalendarTestRow> Tests
    );

    public interface ISyncRepository
    {
        bool IsConfigured { get; }
        Task<ChildRecord?> GetChildAsync(Guid childId, CancellationToken ct);
        Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct);
        Task<List<SubjectRow>> GetSubjectsAsync(CancellationToken ct);
        Task<Guid> StartRunAsync(Guid childId, Guid familyId, Guid actorId, string? version, CancellationToken ct);

        // upisuje podatke i završava sync_runs kao 'ok'; vraća broj stvarno upisanih ocjena
        Task<int> WriteAsync(SyncWrite write, CancellationToken ct);

        Task FinishRunAsync(Guid runId, string status, int changed, string? errorCode, CancellationToken ct);
    }

    // čisti Npgsql kao uloga ednevnik_service; piše samo ono što joj grantovi dopuštaju
    // Supabase baza za /v2/sync, namjerno ne kao goli NpgsqlDataSource u DI-ju
    // (EF Core bi ga onda koristio i za staru ednevnik bazu)
    public sealed record SupabaseDb(NpgsqlDataSource DataSource);

    public class SyncRepository : ISyncRepository
    {
        private readonly NpgsqlDataSource? _db;

        public SyncRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        private NpgsqlDataSource Db => _db ?? throw new InvalidOperationException("Supabase DB not configured.");

        public async Task<ChildRecord?> GetChildAsync(Guid childId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select id, family_id, auth_user_id, self_managed from public.children "
                    + "where id = $1 and deleted_at is null"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return null;
            return new ChildRecord(
                r.GetGuid(0),
                r.GetGuid(1),
                r.IsDBNull(2) ? null : r.GetGuid(2),
                r.GetBoolean(3)
            );
        }

        public async Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select 1 from public.parents where id = $1 and family_id = $2"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = userId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = familyId });
            return await cmd.ExecuteScalarAsync(ct) != null;
        }

        public async Task<List<SubjectRow>> GetSubjectsAsync(CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand("select id, code, name_hr from public.subjects");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var list = new List<SubjectRow>();
            while (await r.ReadAsync(ct))
                list.Add(new SubjectRow(r.GetInt16(0), r.GetString(1), r.GetString(2)));
            return list;
        }

        public async Task<Guid> StartRunAsync(
            Guid childId,
            Guid familyId,
            Guid actorId,
            string? version,
            CancellationToken ct
        )
        {
            await using var cmd = Db.CreateCommand(
                "insert into public.sync_runs (child_id, family_id, actor_id, service_version) "
                    + "values ($1, $2, $3, $4) returning id"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = familyId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = actorId });
            cmd.Parameters.Add(Text(version));
            return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
        }

        public async Task<int> WriteAsync(SyncWrite w, CancellationToken ct)
        {
            await using var conn = await Db.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // child_subjects: followed se nikad ne dira
            foreach (var cs in w.ChildSubjects)
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into public.child_subjects "
                        + "(child_id, family_id, subject_id, school_year, ednevnik_ref, teacher_name) "
                        + "values ($1, $2, $3, $4, $5, $6) "
                        + "on conflict (child_id, subject_id, school_year) do update set "
                        + "family_id = excluded.family_id, ednevnik_ref = excluded.ednevnik_ref, "
                        + "teacher_name = excluded.teacher_name",
                    conn,
                    tx
                );
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.FamilyId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = cs.SubjectId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.Year.Label });
                cmd.Parameters.Add(Text(cs.EdnevnikRef));
                cmd.Parameters.Add(Text(cs.TeacherName));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // ocjene: samo insert, postojeći hash se preskače (trigger javlja samo nove)
            var inserted = 0;
            foreach (var g in w.Grades)
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into public.grades "
                        + "(child_id, family_id, subject_id, grading_element, value, note, graded_on, source_hash) "
                        + "values ($1, $2, $3, $4, $5, $6, $7, $8) on conflict (source_hash) do nothing",
                    conn,
                    tx
                );
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.FamilyId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = g.SubjectId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = g.GradingElement });
                cmd.Parameters.Add(new NpgsqlParameter { Value = g.Value });
                cmd.Parameters.Add(Text(g.Note));
                cmd.Parameters.Add(new NpgsqlParameter { Value = g.GradedOn });
                cmd.Parameters.Add(new NpgsqlParameter { Value = g.SourceHash });
                inserted += await cmd.ExecuteNonQueryAsync(ct);
            }

            // ispiti: obriši e-Dnevnik ispite ove školske godine i upiši svježe
            await using (
                var del = new NpgsqlCommand(
                    "delete from public.calendar_events where child_id = $1 and source = 'ednevnik' "
                        + "and kind = 'test' and event_date between $2 and $3",
                    conn,
                    tx
                )
            )
            {
                del.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                del.Parameters.Add(new NpgsqlParameter { Value = w.Year.FirstDay });
                del.Parameters.Add(new NpgsqlParameter { Value = w.Year.LastDay });
                await del.ExecuteNonQueryAsync(ct);
            }
            foreach (var t in w.Tests)
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into public.calendar_events "
                        + "(child_id, family_id, subject_id, kind, title, event_date, source) "
                        + "values ($1, $2, $3, 'test', $4, $5, 'ednevnik')",
                    conn,
                    tx
                );
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.FamilyId });
                cmd.Parameters.Add(
                    new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Smallint,
                        Value = t.SubjectId is { } sid ? sid : DBNull.Value,
                    }
                );
                cmd.Parameters.Add(new NpgsqlParameter { Value = t.Title });
                cmd.Parameters.Add(new NpgsqlParameter { Value = t.EventDate });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await FinishRunAsync(conn, tx, w.RunId, "ok", inserted, null, ct);
            await tx.CommitAsync(ct);
            return inserted;
        }

        public async Task FinishRunAsync(
            Guid runId,
            string status,
            int changed,
            string? errorCode,
            CancellationToken ct
        )
        {
            await using var conn = await Db.OpenConnectionAsync(ct);
            await FinishRunAsync(conn, null, runId, status, changed, errorCode, ct);
        }

        private static async Task FinishRunAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction? tx,
            Guid runId,
            string status,
            int changed,
            string? errorCode,
            CancellationToken ct
        )
        {
            await using var cmd = new NpgsqlCommand(
                "update public.sync_runs set finished_at = now(), status = $2::public.sync_status, "
                    + "changed = $3, error_code = $4 where id = $1",
                conn,
                tx
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = runId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = status });
            cmd.Parameters.Add(new NpgsqlParameter { Value = changed });
            cmd.Parameters.Add(Text(errorCode));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static NpgsqlParameter Text(string? value) =>
            new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    }
}

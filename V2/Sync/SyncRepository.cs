using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Sync
{
    public record ChildRecord(Guid Id, Guid FamilyId, Guid? AuthUserId, bool SelfManaged);

    // Average: prosjek s popisa predmeta; GradesFetched: stranica ocjena skinuta u ovom syncu
    // (samo tada se pamte prosjek i vrijeme, inače bi se promjena propustila)
    public record ChildSubjectRow(
        short SubjectId,
        string? EdnevnikRef,
        string? TeacherName,
        string? Average = null,
        bool GradesFetched = true
    );

    public record CalendarTestRow(short? SubjectId, string Title, DateOnly EventDate);

    // jedan redak public.timetable_entries; Weekday 1 = ponedjeljak
    public record TimetableRow(short Weekday, short Period, short SubjectId, TimeOnly? StartsAt, TimeOnly? EndsAt);

    // sve što jedan sync piše u području D, u jednoj transakciji
    public record SyncWrite(
        Guid RunId,
        Guid ChildId,
        Guid FamilyId,
        SchoolYear Year,
        List<ChildSubjectRow> ChildSubjects,
        List<GradeRow> Grades,
        List<CalendarTestRow> Tests,
        string? School = null,
        string? ClassName = null,
        // null: raspored nije skinut ni prepoznat, postojeći ostaje; prazno: e-Dnevnik nema rasporeda
        List<TimetableRow>? Timetable = null,
        DateOnly? TimetableFrom = null,
        // koliko je zahtjeva ovaj sync poslao CARNET-u (sync_runs.carnet_requests)
        int? CarnetRequests = null
    );

    public interface ISyncRepository
    {
        bool IsConfigured { get; }
        Task<ChildRecord?> GetChildAsync(Guid childId, CancellationToken ct);
        Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct);
        Task<List<SubjectRow>> GetSubjectsAsync(CancellationToken ct);

        // inkrementalni sync: zadnji viđeni prosjek i vrijeme po e-Dnevnik id-u predmeta
        Task<Dictionary<string, SubjectFetchState>> GetFetchStateAsync(Guid childId, string schoolYear, CancellationToken ct);

        // već upisane ocjene ove školske godine po predmetu (za prosjek predmeta koji nisu skinuti)
        Task<Dictionary<short, List<short>>> GetGradeValuesAsync(Guid childId, SchoolYear year, CancellationToken ct);

        // kad je raspored zadnji put uspješno skinut
        Task<DateTimeOffset?> LastScheduleFetchAsync(Guid childId, CancellationToken ct);

        // public.ednevnik_subject_id: subjects.id za ime iz e-Dnevnika, stvara predmet ako ga nema
        Task<short> EdnevnikSubjectIdAsync(string ednevnikName, CancellationToken ct);
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
            return new ChildRecord(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetBoolean(3));
        }

        public async Task<bool> IsParentInFamilyAsync(Guid userId, Guid familyId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand("select 1 from public.parents where id = $1 and family_id = $2");
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

        public async Task<Dictionary<string, SubjectFetchState>> GetFetchStateAsync(
            Guid childId,
            string schoolYear,
            CancellationToken ct
        )
        {
            await using var cmd = Db.CreateCommand(
                "select ednevnik_ref, ednevnik_average, grades_fetched_at from public.child_subjects "
                    + "where child_id = $1 and school_year = $2 and ednevnik_ref is not null"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = schoolYear });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var state = new Dictionary<string, SubjectFetchState>();
            while (await r.ReadAsync(ct))
                state[r.GetString(0)] = new SubjectFetchState(
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2)
                );
            return state;
        }

        public async Task<Dictionary<short, List<short>>> GetGradeValuesAsync(
            Guid childId,
            SchoolYear year,
            CancellationToken ct
        )
        {
            await using var cmd = Db.CreateCommand(
                "select subject_id, value from public.grades where child_id = $1 and graded_on between $2 and $3"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = year.FirstDay });
            cmd.Parameters.Add(new NpgsqlParameter { Value = year.LastDay });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var values = new Dictionary<short, List<short>>();
            while (await r.ReadAsync(ct))
            {
                var subject = r.GetInt16(0);
                if (!values.TryGetValue(subject, out var list))
                    values[subject] = list = new List<short>();
                list.Add(r.GetInt16(1));
            }
            return values;
        }

        public async Task<DateTimeOffset?> LastScheduleFetchAsync(Guid childId, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand(
                "select max(started_at) from public.sync_runs "
                    + "where child_id = $1 and status = 'ok' and schedule_fetched"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = childId });
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is DateTime dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
                : result as DateTimeOffset?;
        }

        public async Task<short> EdnevnikSubjectIdAsync(string ednevnikName, CancellationToken ct)
        {
            await using var cmd = Db.CreateCommand("select public.ednevnik_subject_id($1)");
            cmd.Parameters.Add(new NpgsqlParameter { Value = ednevnikName });
            var result = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt16(result);
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

            // škola i razred iz e-Dnevnik profila (servis ne smije mijenjati children izravno)
            if (w.School != null || w.ClassName != null)
            {
                await using var cmd = new NpgsqlCommand("select public.ednevnik_set_child_school($1, $2, $3)", conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)w.School ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)w.ClassName ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // prvi sync djeteta: bez obavijesti za svaku ocjenu koja već postoji (trigger to čita)
            await using (var first = new NpgsqlCommand(
                "select set_config('odlikas.first_sync', "
                    + "case when exists (select 1 from public.grades where child_id = $1) then '' else 'on' end, true)",
                conn, tx))
            {
                first.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                await first.ExecuteNonQueryAsync(ct);
            }

            // child_subjects: followed se nikad ne dira; prosjek i vrijeme samo kad je stranica skinuta
            foreach (var cs in w.ChildSubjects)
            {
                await using var cmd = new NpgsqlCommand(
                    "insert into public.child_subjects "
                        + "(child_id, family_id, subject_id, school_year, ednevnik_ref, teacher_name, "
                        + "ednevnik_average, grades_fetched_at) "
                        + "values ($1, $2, $3, $4, $5, $6, case when $8 then $7 end, case when $8 then now() end) "
                        + "on conflict (child_id, subject_id, school_year) do update set "
                        + "family_id = excluded.family_id, ednevnik_ref = excluded.ednevnik_ref, "
                        + "teacher_name = excluded.teacher_name, "
                        + "ednevnik_average = case when $8 then $7 else child_subjects.ednevnik_average end, "
                        + "grades_fetched_at = case when $8 then now() else child_subjects.grades_fetched_at end",
                    conn,
                    tx
                );
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.FamilyId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = cs.SubjectId });
                cmd.Parameters.Add(new NpgsqlParameter { Value = w.Year.Label });
                cmd.Parameters.Add(Text(cs.EdnevnikRef));
                cmd.Parameters.Add(Text(cs.TeacherName));
                cmd.Parameters.Add(Text(cs.Average));
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = cs.GradesFetched });
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

            // raspored: obriši djetetov i upiši svježi
            if (w.Timetable != null)
            {
                await using (var del = new NpgsqlCommand("delete from public.timetable_entries where child_id = $1", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                    await del.ExecuteNonQueryAsync(ct);
                }
                foreach (var t in w.Timetable)
                {
                    await using var cmd = new NpgsqlCommand(
                        "insert into public.timetable_entries "
                            + "(child_id, family_id, weekday, period, subject_id, starts_at, ends_at, valid_from) "
                            + "values ($1, $2, $3, $4, $5, $6, $7, $8)",
                        conn,
                        tx
                    );
                    cmd.Parameters.Add(new NpgsqlParameter { Value = w.ChildId });
                    cmd.Parameters.Add(new NpgsqlParameter { Value = w.FamilyId });
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = t.Weekday });
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = t.Period });
                    cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = t.SubjectId });
                    cmd.Parameters.Add(Time(t.StartsAt));
                    cmd.Parameters.Add(Time(t.EndsAt));
                    cmd.Parameters.Add(new NpgsqlParameter { Value = w.TimetableFrom ?? DateOnly.FromDateTime(DateTime.UtcNow) });
                    await cmd.ExecuteNonQueryAsync(ct);
                }
            }

            await FinishRunAsync(conn, tx, w.RunId, "ok", inserted, null, ct, w.CarnetRequests, w.Timetable != null);
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
            CancellationToken ct,
            int? carnetRequests = null,
            bool scheduleFetched = false
        )
        {
            await using var cmd = new NpgsqlCommand(
                "update public.sync_runs set finished_at = now(), status = $2::public.sync_status, "
                    + "changed = $3, error_code = $4, carnet_requests = $5, schedule_fetched = $6 where id = $1",
                conn,
                tx
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = runId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = status });
            cmd.Parameters.Add(new NpgsqlParameter { Value = changed });
            cmd.Parameters.Add(Text(errorCode));
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Smallint,
                Value = carnetRequests is { } n ? (short)Math.Min(n, short.MaxValue) : DBNull.Value,
            });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = scheduleFetched });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static NpgsqlParameter Time(TimeOnly? value) =>
            new() { NpgsqlDbType = NpgsqlDbType.Time, Value = value is { } v ? v : DBNull.Value };

        private static NpgsqlParameter Text(string? value) =>
            new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    }
}

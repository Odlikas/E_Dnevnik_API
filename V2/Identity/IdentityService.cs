using E_Dnevnik_API.ScrapingServices;
using E_Dnevnik_API.V2.Sync;
using Npgsql;
using NpgsqlTypes;

namespace E_Dnevnik_API.V2.Identity
{
    public interface IEdnevnikIdentityScraper
    {
        // sirovi /personal_data; baca EdnevnikSessionInvalidException ili EdnevnikUnavailableException
        Task<string> FetchProfileHtmlAsync(IReadOnlyList<SyncCookie> cookies, CancellationToken ct);
    }

    // jedan zahtjev prema CARNET-u, isti klijent i provjera mrtve sesije kao /v2/sync
    public class EdnevnikIdentityScraper : IEdnevnikIdentityScraper
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly TimeProvider _time;
        private readonly ILogger<EdnevnikIdentityScraper> _logger;

        public EdnevnikIdentityScraper(TimeProvider time, ILogger<EdnevnikIdentityScraper> logger)
        {
            _time = time;
            _logger = logger;
        }

        public async Task<string> FetchProfileHtmlAsync(IReadOnlyList<SyncCookie> cookies, CancellationToken ct)
        {
            using var client = CarnetHttp.CreateClient(
                EdnevnikSyncScraper.BuildCookies(cookies),
                CarnetRequestSource.UserScrape,
                "identity",
                RequestTimeout,
                outerHandler: new EdnevnikSessionHandler(_time)
            );
            try
            {
                using var response = await client.GetAsync("https://ocjene.skole.hr/personal_data", ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"personal_data returned {(int)response.StatusCode}",
                        null,
                        response.StatusCode
                    );
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (EdnevnikSessionInvalidException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[v2/identity] profile fetch failed: {Type} {Message}", ex.GetType().Name, ex.Message);
                throw new EdnevnikUnavailableException("e-Dnevnik profile fetch failed.", ex);
            }
        }
    }

    public record IdentityBind(
        Guid UserId,
        string Hash,
        string FirstName,
        short Grade,
        string? ClassName,
        string? School,
        short? BirthYear,
        bool NeedsParentConsent
    );

    public record IdentityBindResult(Guid ChildId, Guid FamilyId, bool Created, bool NeedsParentConsent);

    public interface IIdentityRepository
    {
        bool IsConfigured { get; }

        // public.ednevnik_bind_identity; baca IdentityBindException s kodom iz baze
        Task<IdentityBindResult> BindAsync(IdentityBind bind, CancellationToken ct);
    }

    public class IdentityRepository : IIdentityRepository
    {
        private readonly NpgsqlDataSource? _db;

        public IdentityRepository(IServiceProvider services)
        {
            _db = services.GetService<SupabaseDb>()?.DataSource;
        }

        public bool IsConfigured => _db != null;

        public async Task<IdentityBindResult> BindAsync(IdentityBind b, CancellationToken ct)
        {
            if (_db is null)
                throw new InvalidOperationException("Supabase DB not configured.");
            await using var cmd = _db.CreateCommand(
                "select child_id, family_id, created, needs_parent_consent "
                    + "from public.ednevnik_bind_identity($1, $2, $3, $4, $5, $6, $7, $8)"
            );
            cmd.Parameters.Add(new NpgsqlParameter { Value = b.UserId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = b.Hash });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = b.FirstName });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = b.Grade });
            cmd.Parameters.Add(Text(b.ClassName));
            cmd.Parameters.Add(Text(b.School));
            cmd.Parameters.Add(
                new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Smallint,
                    Value = b.BirthYear is { } y ? y : DBNull.Value,
                }
            );
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = b.NeedsParentConsent });
            try
            {
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new IdentityBindResult(r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2), r.GetBoolean(3));
            }
            catch (PostgresException ex) when (ex.MessageText is "already_bound" or "not_a_student_user" or "bad_identity")
            {
                throw new IdentityBindException(ex.MessageText);
            }
        }

        private static NpgsqlParameter Text(string? value) =>
            new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)value ?? DBNull.Value };
    }

    public enum IdentityOutcomeKind
    {
        Ok,
        SessionInvalid,
        Unavailable,
        NotHighSchool,
        Mismatch,
        AlreadyBound,
        BadRequest,
    }

    public record IdentityOutcome(IdentityOutcomeKind Kind, IdentityResponse? Response = null);

    public interface IIdentityService
    {
        bool IsConfigured { get; }

        Task<IdentityOutcome> IdentifyAsync(
            Guid callerId,
            string? username,
            IReadOnlyList<SyncCookie> cookies,
            CancellationToken ct
        );
    }

    // /v2/identity: profil -> provjera korisničkog imena -> hash -> vezanje -> normalni sync
    public class IdentityService : IIdentityService
    {
        // env Identity__Salt (u /etc/odlikas/ednevnik.env); bez njega /v2/identity vraća 503
        public const string SaltKey = "Identity:Salt";
        public const int MinSaltLength = 16;

        private readonly IEdnevnikIdentityScraper _scraper;
        private readonly IIdentityRepository _repo;
        private readonly ISyncService _sync;
        private readonly TimeProvider _time;
        private readonly ILogger<IdentityService> _logger;
        private readonly string? _salt;

        public IdentityService(
            IEdnevnikIdentityScraper scraper,
            IIdentityRepository repo,
            ISyncService sync,
            TimeProvider time,
            IConfiguration config,
            ILogger<IdentityService> logger
        )
        {
            _scraper = scraper;
            _repo = repo;
            _sync = sync;
            _time = time;
            _logger = logger;
            _salt = config[SaltKey];
        }

        public bool IsConfigured => _repo.IsConfigured && _salt is { Length: >= MinSaltLength };

        public async Task<IdentityOutcome> IdentifyAsync(
            Guid callerId,
            string? username,
            IReadOnlyList<SyncCookie> cookies,
            CancellationToken ct
        )
        {
            string html;
            try
            {
                html = await _scraper.FetchProfileHtmlAsync(cookies, ct);
            }
            catch (EdnevnikSessionInvalidException)
            {
                return new IdentityOutcome(IdentityOutcomeKind.SessionInvalid);
            }
            catch (EdnevnikUnavailableException)
            {
                return new IdentityOutcome(IdentityOutcomeKind.Unavailable);
            }

            var profile = IdentityRules.ParseProfile(html);
            if (profile is null)
            {
                _logger.LogWarning("[v2/identity] personal_data had no student name");
                return new IdentityOutcome(IdentityOutcomeKind.Unavailable);
            }
            if (profile.BirthDate is null)
                _logger.LogWarning("[v2/identity] no birth date; {Shape}", IdentityRules.BirthShape(html));

            // korisničko ime: s profila ako ga pokazuje, inače iz zahtjeva, ali samo ako odgovara imenu
            var login = profile.Username;
            if (login is null)
            {
                var sent = username?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(sent))
                    return new IdentityOutcome(IdentityOutcomeKind.BadRequest);
                if (!IdentityRules.UsernameMatchesName(sent, profile.FullName))
                    return new IdentityOutcome(IdentityOutcomeKind.Mismatch);
                login = sent;
            }

            var grade = IdentityRules.HighSchoolGrade(profile.ClassRaw, profile.School);
            if (grade is null)
                return new IdentityOutcome(IdentityOutcomeKind.NotHighSchool);

            var today = SchoolYear.ZagrebToday(_time);
            IdentityBindResult bound;
            try
            {
                bound = await _repo.BindAsync(
                    new IdentityBind(
                        callerId,
                        IdentityRules.Hash(_salt!, login),
                        profile.FirstName,
                        grade.Value,
                        profile.ClassRaw,
                        profile.School,
                        profile.BirthDate is { } b ? (short)b.Year : null,
                        IdentityRules.NeedsParentConsent(profile.BirthDate, today)
                    ),
                    ct
                );
            }
            catch (IdentityBindException ex)
            {
                return new IdentityOutcome(
                    ex.Code == "bad_identity" ? IdentityOutcomeKind.NotHighSchool : IdentityOutcomeKind.AlreadyBound
                );
            }

            // normalni sync, da ocjene budu tu odmah; ako ne uspije, račun ipak vrijedi
            SyncResponse? synced = null;
            try
            {
                var outcome = await _sync.SyncAsync(callerId, bound.ChildId, cookies, ct);
                if (outcome.Kind == SyncOutcomeKind.Ok)
                    synced = outcome.Response;
                else
                    _logger.LogWarning("[v2/identity] first sync for {ChildId}: {Kind}", bound.ChildId, outcome.Kind);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[v2/identity] first sync failed for {ChildId}", bound.ChildId);
            }

            return new IdentityOutcome(
                IdentityOutcomeKind.Ok,
                new IdentityResponse
                {
                    ChildId = bound.ChildId,
                    Created = bound.Created,
                    FirstName = profile.FirstName,
                    FullName = profile.FullName,
                    School = synced?.School ?? profile.School,
                    ClassName = profile.ClassRaw,
                    Program = profile.Program,
                    SubjectCount = synced?.SubjectCount ?? 0,
                    BirthDate = profile.BirthDate?.ToString("yyyy-MM-dd"),
                    NeedsParentConsent = bound.NeedsParentConsent,
                    Synced = synced != null,
                }
            );
        }
    }
}

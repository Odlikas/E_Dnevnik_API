using System.Net;
using System.Text.Json;
using E_Dnevnik_API.V2.Sync;
using Microsoft.AspNetCore.Mvc;

namespace E_Dnevnik_API.Controllers
{
    // POST /v2/sync - tanki sloj: auth, tijelo, pa SyncService
    // namjerno bez [ApiController] da i loš JSON vrati {"error":"bad_request"}
    [Route("v2/sync")]
    public class V2SyncController : ControllerBase
    {
        private const int MaxCookies = 50;
        private const int MaxCookieLength = 4096;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseAuthClient _auth;
        private readonly ISyncRepository _repo;
        private readonly ISyncService _sync;
        private readonly ILogger<V2SyncController> _logger;

        public V2SyncController(
            ISupabaseAuthClient auth,
            ISyncRepository repo,
            ISyncService sync,
            ILogger<V2SyncController> logger
        )
        {
            _auth = auth;
            _repo = repo;
            _sync = sync;
            _logger = logger;
        }

        [HttpPost]
        [RequestSizeLimit(256 * 1024)]
        public async Task<IActionResult> Sync(CancellationToken ct)
        {
            if (!_repo.IsConfigured || !_auth.IsConfigured)
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.NotConfigured);

            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized);
            var token = header["Bearer ".Length..].Trim();
            if (token.Length == 0)
                return Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized);

            Guid? callerId;
            try
            {
                callerId = await _auth.GetUserIdAsync(token, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/sync] Supabase auth unreachable: {Message}", ex.Message);
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.AuthUnavailable);
            }
            if (callerId is null)
                return Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized);

            SyncRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SyncRequest>(Request.Body, Json, ct);
            }
            catch (JsonException)
            {
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            }
            if (!IsValid(body))
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            try
            {
                EdnevnikSyncScraper.BuildCookies(body!.Cookies!);
            }
            catch (Exception ex) when (ex is System.Net.CookieException or ArgumentException)
            {
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            }

            var outcome = await _sync.SyncAsync(callerId.Value, body.ChildId!.Value, body.Cookies!, ct);
            return outcome.Kind switch
            {
                SyncOutcomeKind.Ok => Ok(outcome.Response),
                SyncOutcomeKind.Forbidden => Error(HttpStatusCode.Forbidden, SyncErrorCodes.Forbidden),
                SyncOutcomeKind.SessionInvalid => Error(HttpStatusCode.UnprocessableEntity, SyncErrorCodes.SessionInvalid),
                _ => Error(HttpStatusCode.BadGateway, SyncErrorCodes.Unavailable),
            };
        }

        private static bool IsValid(SyncRequest? body) =>
            body?.ChildId is { } id
            && id != Guid.Empty
            && body.Cookies is { Count: > 0 and <= MaxCookies } cookies
            && cookies.All(c =>
                c != null
                && !string.IsNullOrWhiteSpace(c.Name)
                && c.Name.Length <= MaxCookieLength
                && c.Value != null
                && c.Value.Length <= MaxCookieLength
            );

        private ObjectResult Error(HttpStatusCode status, string code) =>
            StatusCode((int)status, new SyncError { Error = code });
    }
}

using System.Net;
using System.Text.Json;
using E_Dnevnik_API.V2.Identity;
using E_Dnevnik_API.V2.Sync;
using Microsoft.AspNetCore.Mvc;

namespace E_Dnevnik_API.Controllers
{
    // POST /v2/identity - srednjoškolac: anonimni Supabase JWT + kolačići -> HsStudentAccount
    // namjerno bez [ApiController] da i loš JSON vrati {"error":"bad_request"}
    [Route("v2/identity")]
    public class V2IdentityController : ControllerBase
    {
        private const int MaxCookies = 50;
        private const int MaxCookieLength = 4096;
        private const int MaxUsernameLength = 254;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseAuthClient _auth;
        private readonly IIdentityService _identity;
        private readonly ILogger<V2IdentityController> _logger;

        public V2IdentityController(
            ISupabaseAuthClient auth,
            IIdentityService identity,
            ILogger<V2IdentityController> logger
        )
        {
            _auth = auth;
            _identity = identity;
            _logger = logger;
        }

        [HttpPost]
        [RequestSizeLimit(256 * 1024)]
        public async Task<IActionResult> Identify(CancellationToken ct)
        {
            if (!_identity.IsConfigured || !_auth.IsConfigured)
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
                _logger.LogWarning("[v2/identity] Supabase auth unreachable: {Message}", ex.Message);
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.AuthUnavailable);
            }
            if (callerId is null)
                return Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized);

            IdentityRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<IdentityRequest>(Request.Body, Json, ct);
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
            catch (Exception ex) when (ex is CookieException or ArgumentException)
            {
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            }

            var outcome = await _identity.IdentifyAsync(callerId.Value, body.Username, body.Cookies!, ct);
            return outcome.Kind switch
            {
                IdentityOutcomeKind.Ok => Ok(outcome.Response),
                IdentityOutcomeKind.BadRequest => Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest),
                IdentityOutcomeKind.SessionInvalid => Error(HttpStatusCode.UnprocessableEntity, SyncErrorCodes.SessionInvalid),
                IdentityOutcomeKind.NotHighSchool => Error(HttpStatusCode.UnprocessableEntity, IdentityErrorCodes.NotHighSchool),
                IdentityOutcomeKind.Mismatch => Error(HttpStatusCode.Forbidden, IdentityErrorCodes.IdentityMismatch),
                IdentityOutcomeKind.AlreadyBound => Error(HttpStatusCode.Conflict, IdentityErrorCodes.AlreadyBound),
                _ => Error(HttpStatusCode.BadGateway, SyncErrorCodes.Unavailable),
            };
        }

        public static bool IsValid(IdentityRequest? body) =>
            body?.Cookies is { Count: > 0 and <= MaxCookies } cookies
            && (body.Username is null || body.Username.Length <= MaxUsernameLength)
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

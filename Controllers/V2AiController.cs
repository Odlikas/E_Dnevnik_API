using System.Net;
using System.Text.Json;
using E_Dnevnik_API.V2.Ai;
using E_Dnevnik_API.V2.Sync;
using Microsoft.AspNetCore.Mvc;

namespace E_Dnevnik_API.Controllers
{
    // POST /v2/ai/ask - "Pitaj Josu": Supabase JWT djeteta + pitanje -> Josin odgovor
    // namjerno bez [ApiController] da i loš JSON vrati {"error":"bad_request"}
    [Route("v2/ai")]
    public class V2AiController : ControllerBase
    {
        public const int MaxQuestionLength = 500;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseAuthClient _auth;
        private readonly IAskService _ask;
        private readonly ILogger<V2AiController> _logger;

        public V2AiController(ISupabaseAuthClient auth, IAskService ask, ILogger<V2AiController> logger)
        {
            _auth = auth;
            _ask = ask;
            _logger = logger;
        }

        [HttpPost("ask")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Ask(CancellationToken ct)
        {
            if (!_ask.IsConfigured || !_auth.IsConfigured)
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
                _logger.LogWarning("[v2/ai] Supabase auth unreachable: {Message}", ex.Message);
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.AuthUnavailable);
            }
            if (callerId is null)
                return Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized);

            AskRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<AskRequest>(Request.Body, Json, ct);
            }
            catch (JsonException)
            {
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            }
            var question = Clean(body?.Question);
            if (question is null)
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);

            var outcome = await _ask.AskAsync(callerId.Value, question, ct);
            return outcome.Kind switch
            {
                AskOutcomeKind.Ok => Ok(outcome.Response),
                AskOutcomeKind.NotAChild => Error(HttpStatusCode.Forbidden, AiErrorCodes.NotAChild),
                AskOutcomeKind.LimitReached => Error(HttpStatusCode.TooManyRequests, AiErrorCodes.LimitReached),
                _ => Error(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable),
            };
        }

        // obrezano pitanje ili null kad je prazno ili predugo
        public static string? Clean(string? question)
        {
            var q = question?.Trim();
            return string.IsNullOrEmpty(q) || q.Length > MaxQuestionLength ? null : q;
        }

        private ObjectResult Error(HttpStatusCode status, string code) =>
            StatusCode((int)status, new SyncError { Error = code });
    }
}

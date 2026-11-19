using System.Net;
using System.Text.Json;
using E_Dnevnik_API.V2.Ai;
using E_Dnevnik_API.V2.Sync;
using Microsoft.AspNetCore.Mvc;

namespace E_Dnevnik_API.Controllers
{
    // POST /v2/ai/ask ("Pitaj Josu") i POST /v2/ai/photo ("Slikaj i razumij"): Supabase JWT djeteta;
    // POST /v2/ai/homework ("Provjera zadaće"): JWT djeteta; POST /v2/ai/explain ("Objasni Luki"): JWT roditelja
    // namjerno bez [ApiController] da i loš JSON vrati {"error":"bad_request"}
    [Route("v2/ai")]
    public class V2AiController : ControllerBase
    {
        public const int MaxQuestionLength = 500;

        // izrezani dio slike; aplikacija ga smanji na najviše 1600 px
        public const int MaxImageBytes = 6 * 1024 * 1024;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseAuthClient _auth;
        private readonly IAskService _ask;
        private readonly IPhotoService _photo;
        private readonly IExplainService _explain;
        private readonly IHomeworkService _homework;
        private readonly ILogger<V2AiController> _logger;

        public V2AiController(
            ISupabaseAuthClient auth,
            IAskService ask,
            IPhotoService photo,
            IExplainService explain,
            IHomeworkService homework,
            ILogger<V2AiController> logger
        )
        {
            _homework = homework;
            _explain = explain;
            _auth = auth;
            _ask = ask;
            _photo = photo;
            _logger = logger;
        }

        [HttpPost("ask")]
        [RequestSizeLimit(16 * 1024)]
        public async Task<IActionResult> Ask(CancellationToken ct)
        {
            if (!_ask.IsConfigured || !_auth.IsConfigured)
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.NotConfigured);
            var (callerId, denied) = await CallerAsync(ct);
            if (denied is not null)
                return denied;

            var body = await ReadAsync<AskRequest>(ct);
            var question = Clean(body?.Question);
            if (question is null)
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);

            var outcome = await _ask.AskAsync(callerId, question, CleanTask(body?.Task), ct);
            return outcome.Kind switch
            {
                AskOutcomeKind.Ok => Ok(outcome.Response),
                AskOutcomeKind.NotAChild => Error(HttpStatusCode.Forbidden, AiErrorCodes.NotAChild),
                AskOutcomeKind.LimitReached => Error(HttpStatusCode.TooManyRequests, AiErrorCodes.LimitReached),
                _ => Error(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable),
            };
        }

        [HttpPost("photo")]
        [RequestSizeLimit(9 * 1024 * 1024)]
        public async Task<IActionResult> Photo(CancellationToken ct)
        {
            if (!_photo.IsConfigured || !_auth.IsConfigured)
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.NotConfigured);
            var (callerId, denied) = await CallerAsync(ct);
            if (denied is not null)
                return denied;

            var body = await ReadAsync<PhotoRequest>(ct);
            var subject = body?.Subject;
            var image = DecodeImage(body?.Image);
            if (subject is null || !PhotoSubjects.All.Contains(subject) || image is null)
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);

            var outcome = await _photo.ExplainAsync(callerId, subject, image.Value.Bytes, image.Value.Mime, ct);
            return outcome.Kind switch
            {
                PhotoOutcomeKind.Ok => Ok(outcome.Response),
                PhotoOutcomeKind.NotAChild => Error(HttpStatusCode.Forbidden, AiErrorCodes.NotAChild),
                PhotoOutcomeKind.PhotoHelpOff => Error(HttpStatusCode.Forbidden, PhotoErrorCodes.PhotoHelpOff),
                PhotoOutcomeKind.SubjectNotAllowed => Error(HttpStatusCode.Forbidden, PhotoErrorCodes.SubjectNotAllowed),
                PhotoOutcomeKind.LimitReached => Error(HttpStatusCode.TooManyRequests, AiErrorCodes.LimitReached),
                PhotoOutcomeKind.Unreadable => Error(HttpStatusCode.UnprocessableEntity, PhotoErrorCodes.Unreadable),
                _ => Error(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable),
            };
        }

        // "Provjera zadaće": 1 do 5 stranica; aplikacija ih šalje sive, najviše 1280 px
        [HttpPost("homework")]
        [RequestSizeLimit(24 * 1024 * 1024)]
        public async Task<IActionResult> Homework(CancellationToken ct)
        {
            if (!_homework.IsConfigured || !_auth.IsConfigured)
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.NotConfigured);
            var (callerId, denied) = await CallerAsync(ct);
            if (denied is not null)
                return denied;

            var body = await ReadAsync<HomeworkRequest>(ct);
            if (body?.Pages is not { Count: > 0 and <= HomeworkService.MaxPages } raw)
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
            var pages = new List<(byte[] Bytes, string Mime)>();
            foreach (var page in raw)
            {
                if (DecodeImage(page) is not { } image)
                    return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);
                pages.Add(image);
            }

            var outcome = await _homework.CheckAsync(callerId, pages, ct);
            return outcome.Kind switch
            {
                HomeworkOutcomeKind.Ok => Ok(outcome.Response),
                HomeworkOutcomeKind.NotAChild => Error(HttpStatusCode.Forbidden, AiErrorCodes.NotAChild),
                HomeworkOutcomeKind.LimitReached => Error(HttpStatusCode.TooManyRequests, AiErrorCodes.LimitReached),
                HomeworkOutcomeKind.Unreadable => Error(HttpStatusCode.UnprocessableEntity, HomeworkErrorCodes.Unreadable),
                _ => Error(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable),
            };
        }

        // "Objasni Luki": Supabase JWT roditelja; Joso piše bilješku u djetetove Bilješke
        [HttpPost("explain")]
        [RequestSizeLimit(4 * 1024)]
        public async Task<IActionResult> Explain(CancellationToken ct)
        {
            if (!_explain.IsConfigured || !_auth.IsConfigured)
                return Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.NotConfigured);
            var (callerId, denied) = await CallerAsync(ct);
            if (denied is not null)
                return denied;

            var body = await ReadAsync<ExplainRequest>(ct);
            if (body?.NotificationId is not { } notificationId)
                return Error(HttpStatusCode.BadRequest, SyncErrorCodes.BadRequest);

            var outcome = await _explain.ExplainAsync(callerId, notificationId, ct);
            return outcome.Kind switch
            {
                ExplainOutcomeKind.Ok => Ok(outcome.Response),
                ExplainOutcomeKind.NotFound => Error(HttpStatusCode.NotFound, ExplainErrorCodes.NotFound),
                ExplainOutcomeKind.PremiumRequired => Error(HttpStatusCode.PaymentRequired, ExplainErrorCodes.PremiumRequired),
                ExplainOutcomeKind.LimitReached => Error(HttpStatusCode.TooManyRequests, AiErrorCodes.LimitReached),
                ExplainOutcomeKind.NothingToExplain => Error(HttpStatusCode.UnprocessableEntity, ExplainErrorCodes.NothingToExplain),
                _ => Error(HttpStatusCode.ServiceUnavailable, AiErrorCodes.AiUnavailable),
            };
        }

        // obrezano pitanje ili null kad je prazno ili predugo
        public static string? Clean(string? question)
        {
            var q = question?.Trim();
            return string.IsNullOrEmpty(q) || q.Length > MaxQuestionLength ? null : q;
        }

        // zadatak iz kviza, skraćen; bez teksta zadatka nema ni zadatka
        public static AskTask? CleanTask(AskTask? task)
        {
            static string? Cut(string? s, int max)
            {
                var t = s?.Trim();
                return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
            }

            var prompt = Cut(task?.Prompt, 800);
            if (prompt is null)
                return null;
            return new AskTask
            {
                Prompt = prompt,
                Choices = (task!.Choices ?? new()).Select(c => Cut(c, 200)).OfType<string>().Take(8).ToList(),
                Given = Cut(task.Given, 200),
                Answer = Cut(task.Answer, 200),
            };
        }

        // base64 JPEG/PNG -> bajtovi i MIME po potpisu datoteke; null za sve ostalo
        public static (byte[] Bytes, string Mime)? DecodeImage(string? base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length > MaxImageBytes / 3 * 4 + 4)
                return null;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                return null;
            }
            if (bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return (bytes, "image/jpeg");
            if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return (bytes, "image/png");
            return null;
        }

        private async Task<(Guid CallerId, IActionResult? Denied)> CallerAsync(CancellationToken ct)
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return (default, Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized));
            var token = header["Bearer ".Length..].Trim();
            if (token.Length == 0)
                return (default, Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized));
            try
            {
                var id = await _auth.GetUserIdAsync(token, ct);
                return id is null
                    ? (default, Error(HttpStatusCode.Unauthorized, SyncErrorCodes.Unauthorized))
                    : (id.Value, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/ai] Supabase auth unreachable: {Message}", ex.Message);
                return (default, Error(HttpStatusCode.ServiceUnavailable, SyncErrorCodes.AuthUnavailable));
            }
        }

        private async Task<T?> ReadAsync<T>(CancellationToken ct)
            where T : class
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(Request.Body, Json, ct);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private ObjectResult Error(HttpStatusCode status, string code) =>
            StatusCode((int)status, new SyncError { Error = code });
    }
}

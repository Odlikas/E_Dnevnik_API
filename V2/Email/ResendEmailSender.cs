using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace E_Dnevnik_API.V2.Email
{
    // šalje kroz Resend HTTP API (POST https://api.resend.com/emails). Ključ u RESEND_API_KEY
    // (ili Resend:ApiKey); pošiljatelj u EMAIL_FROM, zadano "Odlikaš <info@odlikas.hr>".
    public class ResendEmailSender : IEmailSender
    {
        public const string ApiKeyVar = "RESEND_API_KEY";
        public const string FromVar = "EMAIL_FROM";
        public const string DefaultFrom = "Odlikaš <info@odlikas.hr>";
        public const string HttpClientName = "resend";
        public const string Endpoint = "https://api.resend.com/emails";

        private readonly IHttpClientFactory _http;
        private readonly string? _key;
        private readonly string _from;

        public ResendEmailSender(IHttpClientFactory http, IConfiguration config)
        {
            _http = http;
            var key = config[ApiKeyVar] ?? config["Resend:ApiKey"];
            _key = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
            var from = config[FromVar];
            _from = string.IsNullOrWhiteSpace(from) ? DefaultFrom : from.Trim();
        }

        public bool IsConfigured => _key != null;

        public async Task<EmailSendResult> SendAsync(string to, RenderedEmail email, string idempotencyKey, CancellationToken ct)
        {
            if (_key == null)
                return new EmailSendResult(EmailSendStatus.Retry, "nema " + ApiKeyVar);

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    from = _from,
                    to = new[] { to },
                    subject = email.Subject,
                    html = email.Html,
                    text = email.Text,
                }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

            HttpResponseMessage res;
            try
            {
                res = await _http.CreateClient(HttpClientName).SendAsync(req, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return new EmailSendResult(EmailSendStatus.Retry, "mreža: " + ex.GetType().Name);
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                    return new EmailSendResult(EmailSendStatus.Sent);
                var body = await SafeBody(res, ct);
                return new EmailSendResult(StatusOf(res.StatusCode), "resend " + (int)res.StatusCode + ": " + body);
            }
        }

        // 400/422 = ovaj e-mail ne valja (adresa, sadržaj): ne ponavlja se. 401/403 = ključ ili domena
        // (vrijedi za sve e-mailove, popravi se na serveru), 409 = isti Idempotency-Key još traje,
        // 429 i 5xx = privremeno: ponovno kasnije.
        public static EmailSendStatus StatusOf(HttpStatusCode code) =>
            code is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                ? EmailSendStatus.Failed
                : EmailSendStatus.Retry;

        private static async Task<string> SafeBody(HttpResponseMessage res, CancellationToken ct)
        {
            try
            {
                var s = await res.Content.ReadAsStringAsync(ct);
                return s.Length <= 300 ? s : s[..300];
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}

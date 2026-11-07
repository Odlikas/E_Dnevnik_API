using HtmlAgilityPack;

namespace E_Dnevnik_API.V2.Sync
{
    // prepoznaje da nas je e-Dnevnik vratio na prijavu, tj. da je kolačić mrtav
    public static class EdnevnikSessionDetector
    {
        public static bool IsLoginPage(Uri? finalUri, string? html)
        {
            if (finalUri != null)
            {
                var path = finalUri.AbsolutePath.TrimEnd('/');
                if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith("/login/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (string.IsNullOrEmpty(html) || !html.Contains("csrf_token", StringComparison.Ordinal))
                return false;

            // samo forma za prijavu: csrf_token zajedno s poljem za lozinku
            // (druge forme na stranicama mogu imati csrf_token, a da sesija radi)
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return doc.DocumentNode.SelectSingleNode(
                    "//form[.//input[@name='csrf_token'] and .//input[@type='password' or @name='password']]"
                ) != null;
        }
    }

    // za /v2/sync: drži ~500 ms između zahtjeva prema CARNET-u i baca čim dođemo na prijavu
    public class EdnevnikSessionHandler : DelegatingHandler
    {
        public static readonly TimeSpan MinGap = TimeSpan.FromMilliseconds(500);

        private readonly TimeProvider _time;
        private readonly TimeSpan _minGap;
        private DateTimeOffset? _last;

        public EdnevnikSessionHandler(TimeProvider time, TimeSpan? minGap = null)
        {
            _time = time;
            _minGap = minGap ?? MinGap;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (_last is { } last)
            {
                var wait = _minGap - (_time.GetUtcNow() - last);
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, _time, cancellationToken);
            }

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            finally
            {
                _last = _time.GetUtcNow();
            }

            var finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
            string? html = null;
            if (response.IsSuccessStatusCode)
            {
                // buffer da scraper poslije može opet pročitati sadržaj
                await response.Content.LoadIntoBufferAsync();
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }

            if (EdnevnikSessionDetector.IsLoginPage(finalUri, html))
            {
                response.Dispose();
                throw new EdnevnikSessionInvalidException();
            }
            return response;
        }
    }
}

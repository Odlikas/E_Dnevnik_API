using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace E_Dnevnik_API.V2.Push
{
    // FCM HTTP v1 za Firebase projekt odlikas-v2 (ne stari v1 projekt iz FIREBASE_SERVICE_ACCOUNT_JSON).
    // Token je Firebase Installation ID s mobitela; HTTP v1 ga prima u message.token.
    public class FcmPushSender : IPushSender
    {
        public const string AppName = "odlikas-v2";
        public const string CredentialsFileVar = "FIREBASE_V2_CREDENTIALS_FILE";
        public const string CredentialsJsonVar = "FIREBASE_V2_SERVICE_ACCOUNT_JSON";

        private readonly FirebaseMessaging? _messaging;
        private readonly ILogger<FcmPushSender> _logger;

        public FcmPushSender(IConfiguration config, ILogger<FcmPushSender> logger)
        {
            _logger = logger;
            _messaging = Create(config, logger);
        }

        public bool IsConfigured => _messaging != null;

        private static FirebaseMessaging? Create(IConfiguration config, ILogger logger)
        {
            var file = config[CredentialsFileVar];
            var json = config[CredentialsJsonVar];
            if (string.IsNullOrWhiteSpace(file) && string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                var app = FirebaseApp.GetInstance(AppName)
                    ?? FirebaseApp.Create(
                        new AppOptions
                        {
                            Credential = !string.IsNullOrWhiteSpace(file)
                                ? GoogleCredential.FromFile(file)
                                : GoogleCredential.FromJson(json),
                        },
                        AppName
                    );
                return FirebaseMessaging.GetMessaging(app);
            }
            catch (Exception ex)
            {
                // bez detalja vjerodajnica u logu
                logger.LogError("[push] Firebase v2 se nije pokrenuo ({Type}); pushevi su isključeni.", ex.GetType().Name);
                return null;
            }
        }

        public async Task<IReadOnlyList<SendResult>> SendAsync(IReadOnlyList<OutgoingPush> pushes, CancellationToken ct)
        {
            var messaging = _messaging ?? throw new InvalidOperationException("Firebase v2 not configured.");
            var results = new List<SendResult>();
            // SendEach prima najviše 500 poruka
            foreach (var chunk in pushes.Chunk(500))
            {
                var messages = chunk.Select(ToMessage).ToList();
                var response = await messaging.SendEachAsync(messages, ct);
                for (var i = 0; i < chunk.Length; i++)
                {
                    var r = response.Responses[i];
                    results.Add(new SendResult(chunk[i].Token, r.IsSuccess ? SendStatus.Sent : StatusOf(r.Exception)));
                    if (!r.IsSuccess)
                        _logger.LogInformation(
                            "[push] FCM odbio poruku ({Code}) za {Kind} …{Tail}",
                            r.Exception?.MessagingErrorCode,
                            chunk[i].Kind,
                            Tail(chunk[i].Token)
                        );
                }
            }
            return results;
        }

        public static Message ToMessage(OutgoingPush p) =>
            new()
            {
                Token = p.Token,
                Notification = new Notification { Title = p.Title, Body = p.Body },
                Data = p.Data,
                Android = new AndroidConfig { Priority = Priority.High },
                Apns = new ApnsConfig { Aps = new Aps { Sound = "default" } },
            };

        // Unregistered / SenderIdMismatch / "nije valjan token" = token više ne vrijedi
        public static SendStatus StatusOf(FirebaseMessagingException? ex) =>
            ex?.MessagingErrorCode switch
            {
                MessagingErrorCode.Unregistered => SendStatus.DeadToken,
                MessagingErrorCode.SenderIdMismatch => SendStatus.DeadToken,
                MessagingErrorCode.InvalidArgument
                    when ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) => SendStatus.DeadToken,
                MessagingErrorCode.Unavailable => SendStatus.Retry,
                MessagingErrorCode.Internal => SendStatus.Retry,
                MessagingErrorCode.QuotaExceeded => SendStatus.Retry,
                _ => SendStatus.Failed,
            };

        public static string Tail(string token) => token.Length <= 6 ? "" : token[^6..];
    }
}

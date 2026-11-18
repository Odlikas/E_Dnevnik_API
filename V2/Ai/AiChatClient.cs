using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace E_Dnevnik_API.V2.Ai
{
    public interface IAiChatClient
    {
        bool IsConfigured { get; }
        string Provider { get; }

        // baca HttpRequestException / TaskCanceledException kad model ne odgovori
        Task<AiReply> AskAsync(string system, IReadOnlyList<ChatTurn> history, string question, CancellationToken ct);

        // slika zadatka (JPEG/PNG bajtovi) + upute -> sirovi tekst modela i tokeni
        Task<AiCompletion> ReadImageAsync(string system, string instruction, byte[] image, string mime, CancellationToken ct);

        // jedan sustavni i jedan korisnički tekst, modeli za razgovor (npr. "Objasni Luki")
        Task<AiCompletion> WriteAsync(string system, string instruction, CancellationToken ct);
    }

    // Model = koji je model odgovorio (ide u ai_calls.provider)
    public record AiCompletion(string Content, int InputTokens, int OutputTokens, string Model = "");

    // OpenAI-kompatibilni /chat/completions (freellmapi sada; drugi pružatelj = samo env postavke)
    public class AiChatClient : IAiChatClient
    {
        public const string HttpClientName = "ai";

        // svi pokušaji jednog pitanja ili slike zajedno
        public static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(75);

        private readonly IHttpClientFactory _factory;
        private readonly AiOptions _options;

        public AiChatClient(IHttpClientFactory factory, IOptions<AiOptions> options)
        {
            _factory = factory;
            _options = options.Value;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
        public string Provider => _options.Provider;

        public async Task<AiReply> AskAsync(
            string system,
            IReadOnlyList<ChatTurn> history,
            string question,
            CancellationToken ct
        )
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            foreach (var turn in history)
                messages.Add(new JsonObject { ["role"] = turn.FromChild ? "user" : "assistant", ["content"] = turn.Text });
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = question });
            var c = await CompleteAsync(messages, 700, AiOptions.Split(_options.ChatModels), ct);
            return new AiReply(AskAnswerParser.Parse(c.Content), c.InputTokens, c.OutputTokens, c.Model);
        }

        public Task<AiCompletion> ReadImageAsync(
            string system,
            string instruction,
            byte[] image,
            string mime,
            CancellationToken ct
        )
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = instruction },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(image)}" },
                        },
                    },
                },
            };
            return CompleteAsync(messages, 4000, AiOptions.Split(_options.VisionModels), ct);
        }

        public Task<AiCompletion> WriteAsync(string system, string instruction, CancellationToken ct)
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = instruction },
            };
            return CompleteAsync(messages, 2500, AiOptions.Split(_options.ChatModels), ct);
        }

        // OpenAI /chat/completions s prvim modelom s popisa koji odgovori; svaki pokušaj ima svoj rok
        private async Task<AiCompletion> CompleteAsync(
            JsonArray messages,
            int maxTokens,
            IReadOnlyList<string> models,
            CancellationToken ct
        )
        {
            if (models.Count == 0)
                throw new HttpRequestException("no ai models configured");
            Exception? last = null;
            var started = DateTime.UtcNow;
            foreach (var model in models)
            {
                // telefon čeka najviše ~2 min; ne počinji novi pokušaj kad je vrijeme skoro isteklo
                if (DateTime.UtcNow - started > TotalBudget)
                    break;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(_options.AttemptSeconds));
                try
                {
                    return await CompleteWithAsync(model, messages, maxTokens, attempt.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
                {
                    last = ex;
                }
            }
            throw last as HttpRequestException ?? new HttpRequestException("every ai model failed", last);
        }

        private async Task<AiCompletion> CompleteWithAsync(string model, JsonArray messages, int maxTokens, CancellationToken ct)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["temperature"] = 0.3,
                ["max_tokens"] = maxTokens,
            };

            var client = _factory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"ai {model} answered {(int)response.StatusCode}", null, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            var choice = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                    ? choices[0]
                    : default;
            var content = choice.ValueKind == JsonValueKind.Object
                && choice.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var text)
                && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(content))
                throw new HttpRequestException($"ai {model} answered without content");
            // odrezan odgovor (max_tokens) nije valjan JSON; bolje sljedeći model
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String && finish.GetString() == "length")
                throw new HttpRequestException($"ai {model} ran out of tokens");

            int Tokens(string name) =>
                root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.TryGetInt32(out var n) ? n : 0;
            return new AiCompletion(content, Tokens("prompt_tokens"), Tokens("completion_tokens"), model);
        }
    }
}

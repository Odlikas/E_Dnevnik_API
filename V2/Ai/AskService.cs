using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace E_Dnevnik_API.V2.Ai
{
    public enum AskOutcomeKind
    {
        Ok,
        NotAChild,
        LimitReached,
        AiUnavailable,
    }

    public record AskOutcome(AskOutcomeKind Kind, AskResponse? Response = null);

    public interface IAskService
    {
        bool IsConfigured { get; }
        Task<AskOutcome> AskAsync(Guid authUserId, string question, CancellationToken ct);
    }

    // "Pitaj Josu": limit -> model -> upis pitanja i odgovora; neuspjelo pitanje se ne broji
    public class AskService : IAskService
    {
        // koliko zadnjih poruka današnjeg razgovora ide modelu kao kontekst
        private const int HistoryTurns = 6;

        private readonly IAiRepository _repo;
        private readonly IAiChatClient _ai;
        private readonly AiOptions _options;
        private readonly TimeProvider _time;
        private readonly ILogger<AskService> _logger;

        public AskService(
            IAiRepository repo,
            IAiChatClient ai,
            IOptions<AiOptions> options,
            TimeProvider time,
            ILogger<AskService> logger
        )
        {
            _repo = repo;
            _ai = ai;
            _options = options.Value;
            _time = time;
            _logger = logger;
        }

        public bool IsConfigured => _repo.IsConfigured && _ai.IsConfigured;

        public async Task<AskOutcome> AskAsync(Guid authUserId, string question, CancellationToken ct)
        {
            var asker = await _repo.GetAskerAsync(authUserId, ct);
            if (asker is null)
                return new AskOutcome(AskOutcomeKind.NotAChild);

            var since = AskDay.StartOf(_time.GetUtcNow());
            var limit = asker.Premium ? _options.PremiumQuestionsPerDay : _options.FreeQuestionsPerDay;
            var asked = await _repo.CountQuestionsAsync(asker.ChildId, since, ct);
            if (asked >= limit)
                return new AskOutcome(AskOutcomeKind.LimitReached);

            var history = await _repo.RecentAsync(asker.ChildId, since, HistoryTurns, ct);
            var watch = Stopwatch.StartNew();
            AiReply reply;
            try
            {
                reply = await _ai.AskAsync(JosoPrompt.System(asker.Grade, asker.SchoolLevel), history, question, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/ai] model failed for family {FamilyId}: {Message}", asker.FamilyId, ex.Message);
                await _repo.LogCallAsync(asker.FamilyId, _ai.Provider, false, 0, 0, (int)watch.ElapsedMilliseconds, ct);
                return new AskOutcome(AskOutcomeKind.AiUnavailable);
            }

            await _repo.SaveAsync(asker, question, reply.Message, ct);
            await _repo.LogCallAsync(
                asker.FamilyId,
                _ai.Provider,
                true,
                reply.InputTokens,
                reply.OutputTokens,
                (int)watch.ElapsedMilliseconds,
                ct
            );
            return new AskOutcome(
                AskOutcomeKind.Ok,
                new AskResponse
                {
                    Messages = { reply.Message },
                    QuestionsLeft = asker.Premium ? null : limit - asked - 1,
                }
            );
        }
    }
}

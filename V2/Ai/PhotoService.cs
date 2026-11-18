using System.Diagnostics;

namespace E_Dnevnik_API.V2.Ai
{
    public enum PhotoOutcomeKind
    {
        Ok,
        NotAChild,
        PhotoHelpOff,
        SubjectNotAllowed,
        LimitReached,
        Unreadable,
        AiUnavailable,
    }

    public record PhotoOutcome(PhotoOutcomeKind Kind, PhotoResponse? Response = null);

    public interface IPhotoService
    {
        bool IsConfigured { get; }
        Task<PhotoOutcome> ExplainAsync(Guid authUserId, string subject, byte[] image, string mime, CancellationToken ct);
    }

    // "Slikaj i razumij": roditeljeva pravila -> model s vidom -> photo_explanations; slika se ne sprema
    public class PhotoService : IPhotoService
    {
        // kočnica uz roditeljev dnevni limit (najviše 20 u aplikaciji)
        public const int HardDailyCap = 20;

        private readonly IAiRepository _repo;
        private readonly IAiChatClient _ai;
        private readonly TimeProvider _time;
        private readonly ILogger<PhotoService> _logger;

        public PhotoService(IAiRepository repo, IAiChatClient ai, TimeProvider time, ILogger<PhotoService> logger)
        {
            _repo = repo;
            _ai = ai;
            _time = time;
            _logger = logger;
        }

        public bool IsConfigured => _repo.IsConfigured && _ai.IsConfigured;

        public async Task<PhotoOutcome> ExplainAsync(
            Guid authUserId,
            string subject,
            byte[] image,
            string mime,
            CancellationToken ct
        )
        {
            var asker = await _repo.GetAskerAsync(authUserId, ct);
            if (asker is null)
                return new PhotoOutcome(PhotoOutcomeKind.NotAChild);
            var rules = await _repo.GetPhotoRulesAsync(asker.ChildId, ct);
            if (!rules.Enabled)
                return new PhotoOutcome(PhotoOutcomeKind.PhotoHelpOff);
            if (!PhotoSubjects.Allowed(subject, rules.Subjects))
                return new PhotoOutcome(PhotoOutcomeKind.SubjectNotAllowed);
            var taken = await _repo.CountPhotosAsync(asker.ChildId, AskDay.StartOf(_time.GetUtcNow()), ct);
            if (taken >= Math.Min(rules.DailyCap, HardDailyCap))
                return new PhotoOutcome(PhotoOutcomeKind.LimitReached);

            var watch = Stopwatch.StartNew();
            AiCompletion completion;
            try
            {
                completion = await _ai.ReadImageAsync(
                    PhotoPrompt.System(asker.Grade, asker.SchoolLevel, subject, rules.Level),
                    PhotoPrompt.Instruction,
                    image,
                    mime,
                    ct
                );
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("[v2/ai/photo] model failed for family {FamilyId}: {Message}", asker.FamilyId, ex.Message);
                await _repo.LogCallAsync(
                    new AiCall(asker.FamilyId, "photo", _ai.Provider, false, 1, 0, 0, (int)watch.ElapsedMilliseconds),
                    ct
                );
                return new PhotoOutcome(PhotoOutcomeKind.AiUnavailable);
            }

            var result = PhotoAnswerParser.Parse(completion.Content);
            await _repo.LogCallAsync(
                new AiCall(
                    asker.FamilyId,
                    "photo",
                    _ai.Provider,
                    result is not null,
                    1,
                    completion.InputTokens,
                    completion.OutputTokens,
                    (int)watch.ElapsedMilliseconds
                ),
                ct
            );
            if (result is null)
                return new PhotoOutcome(PhotoOutcomeKind.Unreadable);

            var id = await _repo.SavePhotoAsync(asker, subject, result, ct);
            return new PhotoOutcome(
                PhotoOutcomeKind.Ok,
                new PhotoResponse
                {
                    Id = id,
                    Subject = subject,
                    Expression = result.Expression,
                    Topic = result.Topic,
                    Answer = result.Answer,
                    Steps = result.Steps,
                    SimilarTasks = result.SimilarTasks,
                }
            );
        }
    }
}

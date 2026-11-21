using System.Net;

namespace EraTrans.Core;

/// <summary>
/// Sends Chat Completions requests. The reasoning argument is optional so a
/// caller that never enables reasoning keeps the previous behavior.
/// </summary>
public static class AiReasoningFallback
{
    /// <summary>
    /// A rejected reasoning field fails the whole request, while the same request
    /// without it usually succeeds. Only 400/422 are treated as "the reasoning
    /// shape was wrong", because every other status has its own meaning.
    /// </summary>
    public static bool IsRejection(Exception exception, AiReasoningOptions? reasoning) =>
        AiReasoning.IsActive(reasoning) &&
        exception is HttpRequestException
        {
            StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
        };
}

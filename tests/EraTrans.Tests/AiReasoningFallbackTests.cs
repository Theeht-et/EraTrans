using System.Net;
using EraTrans.Core;
using Xunit;

namespace EraTrans.Tests;

public sealed class AiReasoningFallbackTests
{
    [Fact]
    public async Task RejectedReasoning_RetriesOnceWithoutItAndSucceeds()
    {
        var client = new RecordingClient(RejectMode.OnlyWithReasoning);
        var service = new AiTranslationService(client);

        WindowTranslationOutcome outcome = await service.TranslateWindowAsync(
            CreateProfile(), Window(), new AiReasoningOptions(AiReasoningMode.On));

        Assert.Equal("译文", Assert.Single(outcome.Translations).Translation);
        Assert.Equal(2, client.Attempts.Count);
        Assert.Equal(AiReasoningMode.On, client.Attempts[0]!.Mode);
        Assert.Equal(AiReasoningMode.Auto, client.Attempts[1]!.Mode);
    }

    [Fact]
    public async Task ClientErrorWithoutReasoning_IsNotRetried()
    {
        // The provider fails for an unrelated reason: with reasoning inactive the
        // service must surface the error instead of silently sending twice.
        var client = new RecordingClient(RejectMode.Always);
        var service = new AiTranslationService(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.TranslateWindowAsync(CreateProfile(), Window(), AiReasoningOptions.Auto));

        Assert.Single(client.Attempts);
    }

    [Fact]
    public async Task SuccessfulReasoningRequest_IsSentOnce()
    {
        var client = new RecordingClient(RejectMode.Never);
        var service = new AiTranslationService(client);

        await service.TranslateWindowAsync(
            CreateProfile(), Window(), new AiReasoningOptions(AiReasoningMode.Off));

        Assert.Single(client.Attempts);
        Assert.Equal(AiReasoningMode.Off, client.Attempts[0]!.Mode);
    }

    [Fact]
    public void IsRejection_OnlyTreatsBadRequestAndUnprocessableAsReasoningFailures()
    {
        Assert.True(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.BadRequest),
            new AiReasoningOptions(AiReasoningMode.On)));
        Assert.True(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.UnprocessableEntity),
            new AiReasoningOptions(AiReasoningMode.Off)));
        Assert.False(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.Unauthorized),
            new AiReasoningOptions(AiReasoningMode.On)));
        Assert.False(AiReasoningFallback.IsRejection(
            new HttpRequestException("bad", null, HttpStatusCode.BadRequest),
            AiReasoningOptions.Auto));
        Assert.False(AiReasoningFallback.IsRejection(
            new InvalidOperationException("bad"),
            new AiReasoningOptions(AiReasoningMode.On)));
    }

    private static TranslationWindow Window() =>
        new("@A", 1,
        [
            new WindowLine(1, "文本1", "原文", "@A",
                Translate: true, Reference: false, Join: 0, InlineCondition: false, AltGroup: null)
        ]);

    private static AiProviderProfile CreateProfile() => new()
    {
        Name = "测试服务",
        BaseUrl = "https://api.deepseek.com",
        Model = "deepseek-flash",
        ApiKey = "secret"
    };

    private enum RejectMode
    {
        Never,
        OnlyWithReasoning,
        Always
    }

    private sealed class RecordingClient(RejectMode mode) : IAiChatCompletionClient
    {
        public List<AiReasoningOptions?> Attempts { get; } = [];

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken) =>
            CompleteAsync(profile, systemPrompt, userPrompt, null, cancellationToken);

        public Task<string> CompleteAsync(
            AiProviderProfile profile,
            string systemPrompt,
            string userPrompt,
            AiReasoningOptions? reasoning,
            CancellationToken cancellationToken = default)
        {
            Attempts.Add(reasoning);

            bool reject = mode switch
            {
                RejectMode.Always => true,
                RejectMode.OnlyWithReasoning => AiReasoning.IsActive(reasoning),
                _ => false
            };

            if (reject)
            {
                throw new HttpRequestException(
                    "服务商返回 HTTP 400。", null, HttpStatusCode.BadRequest);
            }

            return Task.FromResult("""{"1":"译文"}""");
        }
    }
}

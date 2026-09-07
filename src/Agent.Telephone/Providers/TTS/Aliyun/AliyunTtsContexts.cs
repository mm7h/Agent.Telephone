using Agent.Telephone.Common.Contexts;

namespace Agent.Telephone.Providers.TTS.Aliyun
{
    internal sealed record AliyunRealtimeTtsOptions(
        string Endpoint,
        string ModelName,
        string Voice,
        int Volume,
        float Rate,
        float Pitch,
        string[] LanguageHints,
        string? Instruction,
        int ConnectionTimeoutSeconds,
        int ResponseTimeoutSeconds);

    internal sealed record AliyunRealtimeTtsSegment(
        string Content,
        string? SentenceId,
        bool IsFirstSegment,
        bool IsLastSegment)
    {
        public static AliyunRealtimeTtsSegment From(OutSegment segment) => new(
            segment.Content,
            segment.SentenceId,
            segment.IsFirstSegment,
            segment.IsLastSegment);
    }

    internal sealed record AliyunRealtimeTtsSentence(
        string EventId,
        string? SaveKey,
        string Text);

    internal sealed record AliyunHttpTtsOptions(
        string Endpoint,
        string ApiKey,
        string ModelName,
        string Voice,
        bool Streaming,
        int Volume,
        float Rate,
        float Pitch,
        string[] LanguageHints,
        string? Instruction,
        int ResponseTimeoutSeconds);

    internal sealed record AliyunHttpTtsResponse(
        string? RequestId,
        AliyunHttpTtsOutput? Output,
        string? Code,
        string? Message);

    internal sealed record AliyunHttpTtsOutput(
        string? FinishReason,
        string? Type,
        string? OriginalText,
        AliyunHttpTtsAudio? Audio);

    internal sealed record AliyunHttpTtsAudio(string? Data, string? Url, string? Id, long? ExpiresAt);
}

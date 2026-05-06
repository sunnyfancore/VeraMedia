namespace VeraMedia.Api.Contracts;

public sealed record CreateConversationRequest(string? Title);
public sealed record ConversationSummary(long Id, string Title, DateTime UpdatedAt);
public sealed record MessageDto(long Id, string Role, string Content, DateTime CreatedAt, string? MessageType = null);
public sealed record SendMessageRequest(string Content, long? ConversationId, IReadOnlyList<AttachmentDto>? Attachments, AgentOptionsDto? Options);
public sealed record GenerationJobDto(
    long Id,
    long ConversationId,
    long UserMessageId,
    long AssistantMessageId,
    string Status,
    string Content,
    IReadOnlyList<string> Thinking,
    string? MessageType,
    string? Error,
    int Version,
    DateTime UpdatedAt);
public sealed record GenerationJobSummaryDto(
    long Id,
    long ConversationId,
    string ConversationTitle,
    long UserMessageId,
    long AssistantMessageId,
    string Status,
    string JobType,
    string RequestPreview,
    string ContentPreview,
    string? MessageType,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt);
public sealed record AttachmentDto(string FileName, string ContentType, long Size, string Url);
public sealed record AgentOptionsDto(string ThinkingMode, string Platform, string OutputFormat, decimal Temperature, int ImageCount, bool EnableWebSearch, bool ShowThinking, string? IntentMode = null, string? StylePreset = null, string? ImageRatio = null, string? ImageStyle = null, string? ImageTemplate = null);
public sealed record ConversationIntent(string Type, string Mode, string Title, string Prompt, double Confidence);
public sealed record ProviderRequest(string Name, string BaseUrl, string ApiKey, string ChatModelName, string ImageModelName);
public sealed record ProviderResponse(long Id, string Name, string BaseUrl, string ChatModelName, string ImageModelName, bool Enabled, bool HasApiKey, string ApiKeyPreview);
public sealed record ModelListRequest(string Name, string BaseUrl, string ApiKey);
public sealed record ModelListResponse(IReadOnlyList<string> Models);
public sealed record ProviderTestRequest(string Name, string BaseUrl, string ApiKey, string ChatModelName, string ImageModelName, string TestType);
public sealed record ProviderTestResponse(bool Ok, string Message, string? Detail = null);
public sealed record FileUploadResponse(string FileName, string ContentType, long Size, string Url);
public sealed record RetryImageRequest(string Title, string ArticleMarkdown);
public sealed record RetryImageResponse(string Title, string? Url, string? Error);
public sealed record GenerateImageRequest(string Title, string Prompt);
public sealed record GenerateImageResponse(string Title, string Prompt, string? Url, string? Error, bool IsPartial = false);
public sealed record UpdateMessageRequest(string Content);
public sealed record EditDocumentRequest(string Content, string Instruction, string? Selection);
public sealed record EditDocumentResponse(string Content);

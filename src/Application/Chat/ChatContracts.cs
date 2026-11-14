using FluentValidation;

namespace Application.Chat;

public sealed record CreateConversationRequest(Guid? WorkspaceId, string? Title);

public sealed record AskRequest(string Content);

/// <summary>Rating is "up", "down" or "none" (clears it).</summary>
public sealed record FeedbackRequest(string Rating);

public sealed record ConversationResponse(Guid Id, Guid? WorkspaceId, string Title, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record MessageResponse(
    Guid Id,
    string Role,
    string Content,
    IReadOnlyList<CitationDto> Citations,
    string? Model,
    string? PromptVersion,
    bool FromCache,
    string? Feedback,
    DateTimeOffset CreatedAt);

public sealed record ConversationDetailResponse(ConversationResponse Conversation, IReadOnlyList<MessageResponse> Messages);

public sealed class CreateConversationRequestValidator : AbstractValidator<CreateConversationRequest>
{
    public CreateConversationRequestValidator() => RuleFor(x => x.Title).MaximumLength(200);
}

public sealed class AskRequestValidator : AbstractValidator<AskRequest>
{
    public const int MaxQuestionChars = 4000;

    public AskRequestValidator() => RuleFor(x => x.Content).NotEmpty().MaximumLength(MaxQuestionChars);
}

public sealed class FeedbackRequestValidator : AbstractValidator<FeedbackRequest>
{
    public FeedbackRequestValidator() =>
        RuleFor(x => x.Rating).Must(r => r is "up" or "down" or "none").WithMessage("Rating must be 'up', 'down' or 'none'.");
}

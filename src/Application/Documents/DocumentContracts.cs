using FluentValidation;

namespace Application.Documents;

public sealed record UpdateDocumentRequest(
    string Title,
    IReadOnlyDictionary<string, string>? Metadata,
    uint Version);

public sealed record DocumentResponse(
    Guid Id,
    Guid WorkspaceId,
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Status,
    int ChunkCount,
    string? FailureReason,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    uint Version);

public sealed class UpdateDocumentRequestValidator : AbstractValidator<UpdateDocumentRequest>
{
    public UpdateDocumentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Metadata).Must(m => m is null || m.Count <= 50)
            .WithMessage("Metadata may contain at most 50 entries.");
    }
}

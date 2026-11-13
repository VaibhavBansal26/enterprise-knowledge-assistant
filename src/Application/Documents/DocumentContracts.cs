using FluentValidation;

namespace Application.Documents;

public sealed record CreateDocumentRequest(
    string Title,
    string FileName,
    string ContentType,
    long SizeBytes,
    IReadOnlyDictionary<string, string>? Metadata);

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
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    uint Version);

public sealed class CreateDocumentRequestValidator : AbstractValidator<CreateDocumentRequest>
{
    public const long MaxSizeBytes = 100L * 1024 * 1024;

    public CreateDocumentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.FileName).NotEmpty().MaximumLength(260);
        RuleFor(x => x.ContentType).NotEmpty().MaximumLength(150);
        RuleFor(x => x.SizeBytes).InclusiveBetween(1, MaxSizeBytes);
        RuleFor(x => x.Metadata).Must(m => m is null || m.Count <= 50)
            .WithMessage("Metadata may contain at most 50 entries.");
    }
}

public sealed class UpdateDocumentRequestValidator : AbstractValidator<UpdateDocumentRequest>
{
    public UpdateDocumentRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(300);
        RuleFor(x => x.Metadata).Must(m => m is null || m.Count <= 50)
            .WithMessage("Metadata may contain at most 50 entries.");
    }
}

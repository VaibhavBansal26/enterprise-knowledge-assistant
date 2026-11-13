using FluentValidation;

namespace Application.Common;

public sealed record AuditQuery(int Page = 1, int PageSize = 20, string? EntityType = null, Guid? EntityId = null);

public sealed record AuditLogResponse(
    Guid Id,
    string? UserId,
    string Action,
    string EntityType,
    Guid EntityId,
    string? ChangedFields,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record MeResponse(string UserId, Guid TenantId, IReadOnlyList<string> Roles);

public sealed class AuditQueryValidator : AbstractValidator<AuditQuery>
{
    public AuditQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListQueryValidator.MaxPageSize);
        RuleFor(x => x.EntityType).MaximumLength(50);
    }
}

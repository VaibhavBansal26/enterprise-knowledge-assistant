using Domain;
using FluentValidation;

namespace Application.Workspaces;

/// <summary>Role is "Viewer" or "Editor" (case-insensitive).</summary>
public sealed record UpsertMemberRequest(string Role);

public sealed record MemberResponse(string UserId, string Role, DateTimeOffset CreatedAt);

public sealed class UpsertMemberRequestValidator : AbstractValidator<UpsertMemberRequest>
{
    public UpsertMemberRequestValidator()
    {
        RuleFor(x => x.Role)
            .NotEmpty()
            .Must(r => Enum.TryParse<WorkspaceRole>(r, ignoreCase: true, out _) && !int.TryParse(r, out _))
            .WithMessage("Role must be 'Viewer' or 'Editor'.");
    }
}

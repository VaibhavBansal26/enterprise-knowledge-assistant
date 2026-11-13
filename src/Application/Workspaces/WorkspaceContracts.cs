using FluentValidation;

namespace Application.Workspaces;

public sealed record CreateWorkspaceRequest(string Name, string? Description);

public sealed record UpdateWorkspaceRequest(string Name, string? Description, uint Version);

public sealed record WorkspaceResponse(
    Guid Id, string Name, string? Description, DateTimeOffset CreatedAt, uint Version);

public sealed class CreateWorkspaceRequestValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class UpdateWorkspaceRequestValidator : AbstractValidator<UpdateWorkspaceRequest>
{
    public UpdateWorkspaceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

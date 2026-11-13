using FluentValidation;

namespace Application.Common;

public sealed record ListQuery(int Page = 1, int PageSize = 20, string? Q = null, string? Sort = null);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed class ListQueryValidator : AbstractValidator<ListQuery>
{
    public const int MaxPageSize = 100;

    public ListQueryValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, MaxPageSize);
        RuleFor(x => x.Q).MaximumLength(200);
    }
}

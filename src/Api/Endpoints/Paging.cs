using Application.Common;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

internal static class Paging
{
    public static async Task<PagedResult<TResponse>> ToPagedAsync<TEntity, TResponse>(
        this IQueryable<TEntity> query,
        ListQuery list,
        Func<TEntity, TResponse> map,
        CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((list.Page - 1) * list.PageSize)
            .Take(list.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TResponse>(items.Select(map).ToList(), list.Page, list.PageSize, total);
    }

    /// <summary>Parses "field" / "-field" into (field, descending). Defaults to newest first.</summary>
    public static (string Field, bool Descending) ParseSort(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return ("createdAt", true);
        }

        return sort[0] == '-' ? (sort[1..], true) : (sort, false);
    }
}

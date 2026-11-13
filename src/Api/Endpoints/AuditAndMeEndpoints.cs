using System.Security.Claims;
using Api.Auth;
using Api.Web;
using Application.Abstractions;
using Application.Common;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Api.Endpoints;

public static class AuditAndMeEndpoints
{
    public static IEndpointRouteBuilder MapAuditAndMeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me", Me).WithTags("Identity");

        app.MapGet("/api/v1/audit", Audit)
            .WithTags("Audit")
            .RequireAuthorization(Policies.Admin)
            .AddEndpointFilter<ValidationFilter<AuditQuery>>();

        return app;
    }

    private static IResult Me(ClaimsPrincipal user, ICurrentUser me, ITenantContext tenant) =>
        Results.Ok(new MeResponse(
            me.UserId!,
            tenant.TenantId!.Value,
            user.FindAll("roles").Select(c => c.Value).Order(StringComparer.Ordinal).ToList()));

    private static async Task<IResult> Audit([AsParameters] AuditQuery q, AppDbContext db, CancellationToken ct)
    {
        // The tenant query filter scopes this to the caller's own tenant.
        IQueryable<Domain.AuditLog> query = db.AuditLogs.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q.EntityType))
        {
            query = query.Where(a => a.EntityType == q.EntityType);
        }

        if (q.EntityId is { } entityId)
        {
            query = query.Where(a => a.EntityId == entityId);
        }

        query = query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);

        return Results.Ok(new PagedResult<AuditLogResponse>(
            items.Select(a => new AuditLogResponse(
                a.Id, a.UserId, a.Action, a.EntityType, a.EntityId, a.ChangedFields, a.CorrelationId, a.CreatedAt)).ToList(),
            q.Page,
            q.PageSize,
            total));
    }
}

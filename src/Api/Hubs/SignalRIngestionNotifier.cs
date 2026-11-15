using Application.Ingestion;
using Microsoft.AspNetCore.SignalR;

namespace Api.Hubs;

/// <summary>Publishes ingestion progress to subscribed browsers (across instances via the Redis backplane).</summary>
public sealed class SignalRIngestionNotifier(IHubContext<IngestionHub> hub) : IIngestionNotifier
{
    public Task DocumentStatusChangedAsync(DocumentStatusChanged change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        return hub.Clients.Group(IngestionHub.GroupName(change.TenantId, change.WorkspaceId))
            .SendAsync(IngestionHub.StatusEvent, change, cancellationToken);
    }
}

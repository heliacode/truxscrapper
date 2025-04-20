using Microsoft.AspNetCore.SignalR;

using TruxScrapper;

using System.Collections.Concurrent;

namespace TruxScrapper;

public class OrderTrackerHub : Hub
{
    readonly ConcurrentDictionary<string, CancellationTokenSource> cancelByClients = [];

    ~OrderTrackerHub()
    {
        var cancelByClientsKey = cancelByClients.Keys.ToArray().AsSpan();

        foreach (var clientId in cancelByClientsKey)
        {
            if (cancelByClients.TryRemove(clientId, out var cancellor))
            {
                try
                {
                    cancellor.Cancel();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Error removing client {clientId}): {ex.Message}");
                }
            }
        }
    }

    public Task UpdateConnectionIdAsync(string clientName, string[] trackingNumbers)
    {
        var connectionId = Context.ConnectionId;
        try
        {
            try
            {
                if (cancelByClients.TryRemove(connectionId, out var cts))
                {
                    cts.Cancel();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Error removing client {clientName} ({connectionId}): {ex.Message}");
                return Task.CompletedTask;
            }

            CancellationTokenSource cancellator = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);

            cancelByClients.TryAdd(clientName, cancellator);

            return OrderTrackerService.UpdateConnectionIdAsync(
                clientName,
                trackingNumbers,
                (trackingNumber, history) => Clients.Client(connectionId).SendAsync("Update", new { trackingNumber, history }, cancellator.Token));
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Error removing client {clientName} ({connectionId}): {ex.Message}");
            return Task.CompletedTask;
        }
    }
}

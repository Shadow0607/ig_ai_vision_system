using Microsoft.AspNetCore.SignalR;

namespace IgAiBackend.Hubs;

public class MonitorHub : Hub
{
    // 空的即可，主要是讓前端可以連線進來，並讓後端可以透過 IHubContext 廣播
    public override async Task OnConnectedAsync()
    {
        Console.WriteLine($"[SignalR] 監控大盤已連線: {Context.ConnectionId}");
        await base.OnConnectedAsync();
    }
}
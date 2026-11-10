namespace CaseManagement.Api.Controllers;

using System.Text.Json;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;

/// <summary>
/// The live event stream every open Case Management tab listens to (server-sent events). Each event is one line of JSON:
/// <c>{"topic":"agent-status","data":{"userId":"…","status":"Offline"}}</c>. A comment line is sent every 20 seconds so
/// proxies keep the connection open, and the browser reconnects on its own when it drops.
/// </summary>
/// <remarks>Any signed-in Case Management user may listen: an event carries a person's availability, never case data.</remarks>
[ApiController]
[Route("api/live")]
[RequirePermission(Permissions.CasesRead)]
public class LiveController(ILiveEvents live) : BaseApiController
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    [HttpGet]
    public async Task Stream(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";   // nginx: send each event as it is written
        Response.Headers.Connection = "keep-alive";

        using var subscription = live.Subscribe();
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);
                try
                {
                    if (await subscription.Reader.WaitToReadAsync(wait.Token))
                    {
                        while (subscription.Reader.TryRead(out var e))
                            await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { topic = e.Topic, data = e.Data }, Json)}\n\n", ct);
                    }
                    else break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Response.WriteAsync(": ping\n\n", ct);
                }
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

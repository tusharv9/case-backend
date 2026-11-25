namespace CaseManagement.Api.Controllers;

using System.Text.Json;
using CaseManagement.Api.HostIntegration;
using CaseManagement.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The live event stream every open Case Management tab listens to (server-sent events). Each event is one line of JSON:
/// <c>{"topic":"agent-status","data":{"userId":"…","status":"Offline"}}</c>. A comment line is sent every 20 seconds so
/// proxies keep the connection open, and the browser reconnects on its own when it drops.
/// </summary>
/// <remarks>
/// Any signed-in Case Management user may listen (an Inbox-only user included): an event carries a person's availability
/// or the id of an email/ticket that changed, never case or email content. The screen re-reads through the scoped endpoints.
/// </remarks>
[ApiController]
[Route("api/live")]
[RequirePermission(null)]
public class LiveController(ILiveEvents live, CaseManagement.Api.Data.AppDbContext db) : BaseApiController
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    /// <summary>How often an open tab refreshes the person's presence (well inside <see cref="Models.AgentPresence.Window"/>).</summary>
    private static readonly TimeSpan PresenceEvery = TimeSpan.FromSeconds(60);

    /// <summary>Open Case Management tabs per person on this instance: the last one closing makes them Away at once.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> OpenTabs = new();

    [HttpGet]
    public async Task Stream(CancellationToken ct)
    {
        // Send each event the moment it is written; a buffered (or compressed) stream reaches the browser late or never.
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-transform";
        Response.Headers["X-Accel-Buffering"] = "no";   // nginx: send each event as it is written
        Response.Headers.Connection = "keep-alive";

        using var subscription = live.Subscribe();
        await Response.WriteAsync(": connected\n\n", ct);
        await Response.Body.FlushAsync(ct);

        // Presence: while this tab is open the person is here (Available, Busy or Away as they chose); when their last tab closes
        // — signing out of OmniConnect closes it too — they are Away. A tab that vanishes without closing lapses after the window.
        var me = CurrentUserId;
        var tracked = me != Guid.Empty;
        if (tracked && OpenTabs.AddOrUpdate(me, 1, (_, n) => n + 1) == 1) await SeenAsync(me, announce: true);
        else if (tracked) await SeenAsync(me, announce: false);
        var lastSeen = DateTime.UtcNow;

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
                if (tracked && DateTime.UtcNow - lastSeen >= PresenceEvery)
                {
                    lastSeen = DateTime.UtcNow;
                    await SeenAsync(me, announce: false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            if (tracked && OpenTabs.AddOrUpdate(me, 0, (_, n) => Math.Max(0, n - 1)) == 0)
            {
                OpenTabs.TryRemove(new KeyValuePair<Guid, int>(me, 0));
                await GoneAsync(me);
            }
        }
    }

    /// <summary>Marks the person as here now; <paramref name="announce"/> tells the open screens they came back.</summary>
    private async Task SeenAsync(Guid userId, bool announce)
    {
        try
        {
            var now = DateTime.UtcNow;
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now));
            if (announce) Announce(userId, now);
        }
        catch (Exception) { /* presence is best effort: a failed refresh lapses into Away, never an error for the tab */ }
    }

    /// <summary>The person's last tab closed: Away now, rather than after the window.</summary>
    private async Task GoneAsync(Guid userId)
    {
        try
        {
            var away = DateTime.UtcNow - Models.AgentPresence.Window - TimeSpan.FromSeconds(1);
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, away), CancellationToken.None);
            Announce(userId, DateTime.UtcNow);
        }
        catch (Exception) { /* lapses on its own */ }
    }

    private void Announce(Guid userId, DateTime at)
    {
        var status = db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Status, u.LastSeenAt }).FirstOrDefault();
        if (status == null) return;
        var effective = Models.AgentPresence.Effective(status.Status, status.LastSeenAt, DateTime.UtcNow);
        live.Publish(new LiveEvent("agent-status", new { userId, status = Models.AgentPresence.Label(effective), at }));
    }
}

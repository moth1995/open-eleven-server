using Microsoft.Extensions.Logging;
using TenServer.Data.Repositories;
using TenServer.Protocol.Kv;
using TenServer.Server.Configuration;
using TenServer.Server.Dispatch;
using TenServer.Server.State;

namespace TenServer.Server.Handlers;

/// <summary>
/// Commands the client sends on whatever connection it happens to hold. None of them
/// return data, but two of them carry state the client expects the server to remember,
/// which a bare acknowledgement would silently discard.
/// </summary>
public sealed class SessionHandlers(
    IPlayerRepository players,
    PendingLoginStore pendingLogins,
    ILogger<SessionHandlers> log)
{
    /// <summary>
    /// Text-level keepalive, distinct from the binary heartbeat handled in the transport.
    /// MSG_REQAUTH advertises <c>dont_check_heartbeat="NO"</c>, so the client expects the
    /// server to be watching. A bare NOERR is the whole reply; the value is the timestamp.
    /// </summary>
    [Command("CMD_SEND_HEARTBEAT", Roles = ServiceRole.All)]
    public ValueTask<KvMessage[]> Heartbeat(CommandContext ctx)
    {
        ctx.Session.LastActivity = DateTimeOffset.UtcNow;

        if (ctx.Session.AccountId > 0 && ctx.Session.State >= SessionState.Authenticated)
            pendingLogins.RefreshForAccount(ctx.Session.AccountId, ctx.Session.Remote.Address);

        return Reply.Of(ctx.Ok());
    }

    [Command("CMD_SET_LANGUAGE", Roles = ServiceRole.All)]
    public async ValueTask<KvMessage[]> SetLanguage(CommandContext ctx)
    {
        var lang = ctx.Request.GetString("lang");
        if (string.IsNullOrEmpty(lang))
            return [ctx.Ok()];

        ctx.Session.Language = lang;

        if (ctx.Session.Pid > 0 &&
            ctx.Session.AccountId > 0 &&
            await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken) is { } player &&
            player.AccountId == ctx.Session.AccountId &&
            player.Lang != lang)
        {
            player.Lang = lang;
            await players.SaveAsync(ctx.CancellationToken);
            log.LogInformation("Language for pid {Pid} set to {Lang}", player.Pid, lang);
        }

        return [ctx.Ok()];
    }
}

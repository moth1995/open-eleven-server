using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Quick match. Only MSG_QUICK_REQ_MATCHING_START is confirmed as an incoming command;
/// the other payloads below were reconstructed from the client's parser and are exposed
/// as builders so they can be pushed once the trigger is identified.
/// </summary>
public sealed class MatchingHandlers
{
    private const ServiceRole MatchingRoles =
        ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    [Command("MSG_QUICK_REQ_MATCHING_START", Roles = MatchingRoles,
        RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> RequestMatchingStart(CommandContext ctx)
    {
        ctx.Session.State = SessionState.Matching;
        return Reply.Of(ctx.Ok(), MatchingStartResponse(ctx.Rqid));
    }

    /// <summary>
    /// The client rejects this event as ERR_CLIENT_SVRFORMAT unless all six tuning fields
    /// are present, so they are always written together.
    /// </summary>
    public static KvMessage MatchingStartResponse(
        int rqid,
        int tryCount = 3,
        int ping = 100,
        int pingAdd = 50,
        int bandTryCount = 3,
        int band = 128,
        int bandAdd = 64)
        => KvMessage.Ok("MSG_QUICK_MATCHING_START_RES", rqid)
            .Set("tryCount", tryCount)
            .Set("ping", ping)
            .Set("pingAdd", pingAdd)
            .Set("bandTryCount", bandTryCount)
            .Set("band", band)
            .Set("bandAdd", bandAdd);

    /// <summary>NOERR here starts the client's 20-second preconnect phase.</summary>
    public static KvMessage MatchingResult(int rqid, bool success = true)
        => success
            ? KvMessage.Ok("MSG_QUICK_MATCHING_RES", rqid)
            : KvMessage.Err("MSG_QUICK_MATCHING_RES", rqid, "NOMATCH");

    public static KvMessage AcceptMatchOfferResponse(int rqid, bool success = true)
        => success
            ? KvMessage.Ok("MSG_QUICK_ACCEPT_MATCH_OFFER_RES", rqid)
            : KvMessage.Err("MSG_QUICK_ACCEPT_MATCH_OFFER_RES", rqid, "REJECTED");

    /// <summary>
    /// Member roster for a formed match. Field names are the exact ones the client's
    /// parser reads; member data reaches the client through this notice rather than
    /// through the offer response.
    /// </summary>
    public static KvMessage MatchMemberNotice(int rqid, IReadOnlyList<Session> members, int ownerPid)
    {
        var entries = members.Select(m => new KvMessage()
            .Set("is_owner", m.Pid == ownerPid)
            .Set("pid", m.Pid)
            .Set("pname", m.PlayerName)
            .Set("has_guestplayer", false)
            .Set("ex_ip", m.ExternalIp)
            .Set("ex_port", m.ExternalPort)
            .Set("in_ip", m.InternalIp)
            .Set("in_port", m.InternalPort)
            .Set("manner", 3)
            .Set("from_country", 50)
            .Set("from_area", 0)
            .Set("player_name", m.PlayerName)
            .Set("fd_pic_url", "")
            .Set("fd_selfreport_level", "PRO")
            .Set("fd_beginnermark", true)
            .Set("fd_position_want", "CF")
            .Set("fd_desired_position", 255)).ToArray();

        return KvMessage.Ok("MSG_QUICK_MATCH_MEMBER_NOTICE", rqid)
            .Set("is_old_member", false)
            .SetList("member_num", "member", entries);
    }
}

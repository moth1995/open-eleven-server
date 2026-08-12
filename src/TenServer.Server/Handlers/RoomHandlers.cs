using Microsoft.Extensions.Logging;
using TenServer.Protocol.Kv;
using TenServer.Server.Configuration;
using TenServer.Server.Dispatch;
using TenServer.Server.State;

namespace TenServer.Server.Handlers;

/// <summary>
/// Room creation and the member roster. The roster is built from live sessions in the
/// hub, so a second client that joins appears without any change here.
/// </summary>
public sealed class RoomHandlers(
    ProtocolTextPolicy textPolicy,
    ILogger<RoomHandlers> log)
{
    private const ServiceRole RoomRoles = ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    [Command("CMD_CREATEJOIN_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> CreateOrJoinRoom(CommandContext ctx)
    {
        // Keep accepting the older combined form used during early protocol tracing.
        // The room browser sends CMD_JOIN_ROOM for an advertised existing room.
        var requestedId = ctx.Request.GetInt32("room_id", 0);
        if (requestedId > 0)
            return Reply.Of(JoinExistingRoom(ctx, requestedId, includeRoomId: true));

        if (ctx.Session.RoomId is not null)
            return Reply.Of(ctx.Fail("ERR_ALREADYINROOM"));

        var maxPlayers = ctx.Request.GetInt32("max_players", 4);
        var requestedName = ctx.Request.GetString("name") ?? $"Room {ctx.Session.PlayerName}";
        if (ctx.Session is not { Pid: > 0, BlockId: not null }
            || maxPlayers is not (2 or 4)
            || !textPolicy.TryValidate(requestedName, out var name))
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        var room = ctx.Hub.CreateRoom(ctx.Session, name, maxPlayers);

        room.MatchType = ctx.Request.GetString("match_type") ?? "";
        room.TeamCategory = ctx.Request.GetString("team_category") ?? "";
        room.Language = ctx.Request.GetString("lang") ?? ctx.Session.Language;
        room.Password = ctx.Request.GetString("password") ?? "";
        room.AllowGuest = ctx.Request.GetString("enable_guest") != "NO";
        room.InviteLimited = ctx.Request.GetString("is_invite_limit") == "YES";

        ctx.Hub.PublishRoomUpdated(room, ctx.Session.Id);

        return Reply.Of(ctx.Ok().Set("room_id", room.Id));
    }

    [Command("CMD_JOIN_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> JoinRoom(CommandContext ctx)
    {
        // PES2010 FUN_007B8AD0 sends room_id/is_invited/password and advances after the
        // ordinary command result; no room payload is parsed from the success response.
        var roomId = ctx.Request.GetInt32("room_id", 0);
        if (roomId <= 0)
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        return Reply.Of(JoinExistingRoom(ctx, roomId, includeRoomOwner: true));
    }

    /// <summary>
    /// Endpoint of another player, for the peer-to-peer connection the clients make
    /// between themselves.
    /// </summary>
    /// <remarks>
    /// Gated at InBlock, not InRoom: the client asks for the host's endpoint from the room
    /// browser, immediately before CMD_JOIN_ROOM, so requiring InRoom refused the very
    /// request that precedes joining. The answer is still confined to the caller's own
    /// block, so this exposes no endpoint the caller could not already see in the room and
    /// block player lists.
    /// </remarks>
    [Command("CMD_GET_IPANDPORT", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> GetIpAndPort(CommandContext ctx)
    {
        // PES2010 FUN_0072B8A0 parses these five top-level fields.
        var requestedPid = ctx.Request.GetInt32("pid", 0);
        if (requestedPid <= 0 || ctx.Session.BlockId is not { } blockId)
            return Reply.Of(ctx.Fail("ERR_NOPLAYER"));

        // Prefer the caller's own room, so a member is resolved the same way as before
        // when the caller is already inside one; otherwise fall back to the block.
        var room = ctx.Session.RoomId is { } roomId ? ctx.Hub.FindRoom(roomId) : null;
        var member =
            room?.Snapshot().FirstOrDefault(candidate => candidate.Pid == requestedPid)
            ?? ctx.Hub.SessionsInBlock(blockId)
                .FirstOrDefault(candidate =>
                    candidate.Pid == requestedPid && candidate.Role == ctx.Session.Role);

        if (member is null)
            return Reply.Of(ctx.Fail("ERR_NOPLAYER"));

        return Reply.Of(ctx.Ok()
            .Set("ex_ip", member.ExternalIp)
            .Set("ex_port", member.ExternalPort)
            .Set("in_ip", member.InternalIp)
            .Set("in_port", member.InternalPort)
            .Set("pid", member.Pid));
    }

    [Command("MSG_REQROOMLIST", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> RequestRoomList(CommandContext ctx)
    {
        // FUN_00BB9200 requires ACK / START / LIST / END. It reads "roomList[N]" per
        // index, but that string is a path expression evaluated by FUN_00B6E0F0 —
        // list "roomList", element N — not a literal key, and FUN_00B6DD20 finds the
        // element by walking {...} groups. So the bracketed list is the form it reads.
        ctx.Session.RoomListSubscribed = true;
        var rooms = ctx.Hub.RoomsFor(ctx.Session)
            .Select(RoomPresenter.ListEntry)
            .ToArray();

        return Reply.Of(
            ctx.Ok(),
            ctx.Ok("MSG_ROOMLISTSTART"),
            ctx.Ok("MSG_ROOMLIST").SetList("count", "roomList", rooms),
            ctx.Ok("MSG_ROOMLISTEND"));
    }

    [Command("CMD_LEAVE_ROOM", Roles = RoomRoles, RequiredState = SessionState.InBlock)]
    public ValueTask<KvMessage[]> LeaveRoom(CommandContext ctx)
    {
        if (ctx.Session.RoomId is not { } roomId)
            return Reply.Of(ctx.Ok());

        var result = ctx.Hub.LeaveRoom(ctx.Session, roomId);
        return result.Status == RoomLeaveStatus.RoomNotFound
            ? Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"))
            : Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Match settings chosen by the room owner, arriving as a nested record
    /// (<c>game_env={cpuLevel="HARD",gametime="5MINUTES",...}</c>). The client is happy
    /// with a bare NOERR, but the settings have to reach whoever joins the room, so they
    /// are held on the room rather than discarded.
    /// </summary>
    [Command("CMD_SET_GAMEENV", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameEnvironment(CommandContext ctx)
    {
        var environment = ctx.Request.GetValue("game_env") as KvMessage;
        if (environment is null)
        {
            log.LogWarning("CMD_SET_GAMEENV without a game_env record: {Request}", ctx.Request);
            return Reply.Of(ctx.Ok());
        }

        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Err("NOROOM"));

        room.GameEnvironment = environment;
        ctx.Hub.PublishRoomUpdated(room, ctx.Session.Id);

        log.LogInformation(
            "Room {RoomId} settings: gametime={Time} cpu={Cpu} ball={Ball} subs={Subs}",
            room.Id,
            environment.GetString("gametime"),
            environment.GetString("cpuLevel"),
            environment.GetInt32("ball_type"),
            environment.GetInt32("substitution"));

        return Reply.Of(ctx.Ok());
    }

    [Command("CMD_WATCH_ENTRY_GAME", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> WatchEntryGame(CommandContext ctx)
    {
        ctx.Session.GameEntryWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    [Command("CMD_ENTRY_GAME", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> EntryGame(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_ROOMNOTFOUND"));

        var entered = ctx.Request.GetString("entry") switch
        {
            "TRUE" => true,
            "FALSE" => false,
            _ => (bool?)null,
        };
        var side = ctx.Request.GetString("side") switch
        {
            "HOME" => 0,
            "AWAY" => 1,
            _ => -1,
        };

        if (entered is null || (entered.Value && side < 0))
        {
            log.LogWarning(
                "Invalid CMD_ENTRY_GAME from pid {Pid}: entry={Entry} side={Side}",
                ctx.Session.Pid,
                ctx.Request.GetString("entry"),
                ctx.Request.GetString("side"));
            return Reply.Of(ctx.Ok());
        }

        room.SetGameEntry(ctx.Session, entered.Value, side);

        // Complete CMD_ENTRY_GAME before satisfying the sender's parked watch.
        if (!ctx.Session.Push(ctx.Ok()))
            log.LogWarning("Outbound queue full; dropped CMD_ENTRY_GAME ACK for {Session}", ctx.Session);

        var watchRecipients = ctx.Hub.PublishGameEntryChanged(room);
        ctx.Hub.PublishRoomUpdated(room);
        log.LogInformation(
            "pid {Pid} game entry={Entered} side={Side} room={RoomId}; watches={Watches}",
            ctx.Session.Pid, entered.Value, side, room.Id, watchRecipients);

        return Reply.None();
    }

    [Command("MSG_REQRMEMBERLIST", Roles = RoomRoles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> RequestRoomMemberList(CommandContext ctx)
    {
        var room = ctx.Session.RoomId is { } id ? ctx.Hub.FindRoom(id) : null;
        if (room is null)
            return Reply.Of(ctx.Err("NOROOM"));

        var members = room.Snapshot()
            .Select(member => RoomPresenter.MemberListEntry(room, member))
            .ToArray();

        return Reply.Of(
            ctx.Ok(),
            ctx.Ok("MSG_RMEMBERLISTSTART"),
            ctx.Ok("MSG_RMEMBERLIST").SetList("count", "list", members),
            ctx.Ok("MSG_RMEMBEREND"));
    }

    private static KvMessage RoomJoinError(CommandContext ctx, RoomJoinStatus status)
        => status switch
        {
            RoomJoinStatus.RoomNotFound => ctx.Fail("ERR_ROOMNOTFOUND"),
            RoomJoinStatus.Full => ctx.Fail("ERR_ROOMISFULL"),
            RoomJoinStatus.AlreadyInRoom => ctx.Fail("ERR_ALREADYINROOM"),
            RoomJoinStatus.InvalidRoomInfo or RoomJoinStatus.WrongPassword
                => ctx.Fail("ERR_INVALIDROOMINFO"),
            _ => ctx.Fail("ERR_INVALIDROOMINFO"),
        };

    private KvMessage JoinExistingRoom(
        CommandContext ctx,
        int roomId,
        bool includeRoomId = false,
        bool includeRoomOwner = false)
    {
        var joined = ctx.Hub.JoinRoom(
            ctx.Session,
            roomId,
            ctx.Request.GetString("password") ?? "");
        if (joined.Status != RoomJoinStatus.Joined || joined.Room is null)
            return RoomJoinError(ctx, joined.Status);

        ctx.Hub.PublishRoomJoined(joined, ctx.Session);
        log.LogInformation("pid {Pid} joined room {RoomId}", ctx.Session.Pid, joined.Room.Id);
        var response = ctx.Ok();
        if (includeRoomId)
            response.Set("room_id", joined.Room.Id);

        if (includeRoomOwner)
        {
            // PES2010 FUN_00730ED0 stores room_owner.pid as the target for the immediate
            // CMD_GET_IPANDPORT request. Missing this object produces the observed pid=0.
            var owner = joined.Room.Snapshot()
                .First(member => member.Pid == joined.Room.OwnerPid);
            response.Set("room_owner", new KvMessage()
                .Set("name", owner.PlayerName)
                .Set("pid", owner.Pid)
                .Set("xuid", 0L)
                .Set("ex_ip", owner.ExternalIp)
                .Set("ex_port", owner.ExternalPort)
                .Set("in_ip", owner.InternalIp)
                .Set("in_port", owner.InternalPort));
        }

        return response;
    }
}

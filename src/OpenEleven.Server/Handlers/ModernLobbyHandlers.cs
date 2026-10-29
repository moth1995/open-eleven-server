// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The lobby of PES 2011 and later: the server selection screen, entering and leaving a lobby,
/// and the list watches (CMD_REQWATCHL_*) that keep the block roster, the room list and a room's
/// players up to date. PES 2010 has its own lobby handlers; the entries these send are built by
/// <see cref="ListWatchEntries"/>.
/// </summary>
public sealed class ModernLobbyHandlers(
    ServerCatalog catalog,
    IPlayerRepository players,
    ILogger<ModernLobbyHandlers> log)
{
    /// <summary>
    /// The server-selection screen. The response parser (<c>FUN_005ef7c0</c>) reads
    /// <c>lobby_list_num</c> and then <c>lobby_list[i].lobby_id|name|player_num</c>; with none of
    /// those the screen is simply empty. Each entry is a configured Lobby service (its Gid is the
    /// lobby_id); the blocks inside it are what CMD_GET_BLOCKLIST lists, so its player count is
    /// everyone in any block. The request's <c>lobby_type</c> ("OC" in a live run) is not interpreted.
    /// </summary>
    [Command("CMD_GET_LOBBYLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> GetLobbyList(CommandContext ctx)
    {
        var playerNum = ctx.Hub.PlayersInBlocks;
        var entries = LobbyServices.Select(s => new KvMessage()
            .Set("lobby_id", s.Gid)
            .Set("name", s.Name)
            .Set("player_num", playerNum)).ToArray();

        return Reply.Of(ctx.Ok().SetList("lobby_list_num", "lobby_list", entries));
    }

    private IEnumerable<ServiceEndpointOptions> LobbyServices
        => catalog.Endpoints.Where(s => s.Role == ServiceRole.Lobby).OrderBy(s => s.Gid);

    /// <summary>
    /// Block roster / room list subscriptions, sent as a pair right after <c>CMD_JOIN_BLOCK</c>
    /// (<c>FUN_005f6f80</c>). The request names the keys it wants back
    /// (<c>listnum_name</c>, <c>list_name</c>, e.g. <c>blockplayer_list_num</c> /
    /// <c>blockplayer_list</c>); a bare acknowledgement leaves the watcher task in its error
    /// state and the client leaves the lobby with CMD_LEAVE_LOBBY. Answered with an empty list
    /// under exactly those keys until live rosters are pushed.
    /// </summary>
    [Command("CMD_REQWATCHL_BLOCKPLAYERLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WatchBlockPlayers(CommandContext ctx)
    {
        ctx.Session.BlockPlayerWatch = Subscription(ctx, "blockplayer_list_num", "blockplayer_list");

        // One entry per player in the block, the same record later ADD pushes carry.
        var entries = new List<KvMessage>();
        foreach (var pid in ctx.Hub.SessionsInBlock(ctx.Session.BlockId ?? 0)
                     .Where(s => s.Role == ctx.Session.Role && s.Pid > 0).Select(s => s.Pid).Distinct().Take(100))
        {
            var player = await players.GetAsync(pid, ctx.CancellationToken);
            var inRoom = ctx.Hub.SessionsForPid(pid).Select(s => s.RoomId).FirstOrDefault(r => r is not null) ?? 0;
            if (player is not null)
                entries.Add(ListWatchEntries.BlockPlayerEntry(PlayerPresenter.BlockListEntry(player, inRoom, ctx.Profile), ctx.Profile));
        }

        return [WatchReply(ctx, ctx.Session.BlockPlayerWatch, entries)];
    }

    [Command("CMD_REQWATCHL_ROOMLIST", Roles = ServiceRole.Menu, Profiles = GameProfile.Pes2011Pc)]
    public ValueTask<KvMessage[]> WatchRooms(CommandContext ctx)
        => Reply.Of(RoomListReply(ctx, WatchRoomList(ctx)));

    /// <summary>
    /// pes2012.exe's lobby loop (FUN_0057f730) parses the room list before the block roster in
    /// the same frame, and in that mode a room is kept only if its members are already in the
    /// roster cache (FUN_0053bbf0). Both replies arriving together drops every existing room.
    /// </summary>
    [Command("CMD_REQWATCHL_ROOMLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public async ValueTask<KvMessage[]> WatchRoomsAfterRoster(CommandContext ctx)
    {
        var sub = WatchRoomList(ctx);
        // ponytail: fixed delay so the roster lands a frame earlier; queue the reply behind the
        // roster push instead if this ever proves too short.
        await Task.Delay(250, ctx.CancellationToken);
        return [RoomListReply(ctx, sub)];
    }

    private static WatchListSubscription WatchRoomList(CommandContext ctx)
        => ctx.Session.RoomListWatch = Subscription(ctx, "room_list_num", "room_list");

    private static KvMessage RoomListReply(CommandContext ctx, WatchListSubscription sub)
        => WatchReply(ctx, sub, ctx.Hub.RoomsFor(ctx.Session).Take(100).Select(r => ListWatchEntries.RoomEntry(r, ctx.Profile)).ToArray());

    private static WatchListSubscription Subscription(CommandContext ctx, string numDefault, string listDefault)
        => new(ctx.Request.MsgName ?? "", ctx.Rqid,
            ctx.Request.GetString("listnum_name") ?? numDefault,
            ctx.Request.GetString("list_name") ?? listDefault);

    private static KvMessage WatchReply(CommandContext ctx, WatchListSubscription sub, IReadOnlyList<KvMessage> entries)
        => ctx.Ok().Set("list_type", "ADD").SetList(sub.NumName, sub.ListName, entries);

    /// <summary>
    /// The client's own STUN result (pes2011.exe sends it right after CMD_SET_CURRENTPLAYER).
    /// Peers are told these endpoints in CMD_JOIN_ROOM / CMD_GET_IPANDPORT, so without them
    /// every member looks like 127.0.0.1:0 and the P2P connection to the host never starts.
    /// </summary>
    [Command("CMD_SEND_IPANDPORT", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> SendIpAndPort(CommandContext ctx)
    {
        var s = ctx.Session;
        s.ExternalIp = ctx.Request.GetString("ex_ip") ?? s.ExternalIp;
        s.ExternalPort = ctx.Request.GetInt32("ex_port", s.ExternalPort);
        s.InternalIp = ctx.Request.GetString("in_ip") ?? s.InternalIp;
        s.InternalPort = ctx.Request.GetInt32("in_port", s.InternalPort);
        log.LogInformation("pid {Pid} endpoints external={ExIp}:{ExPort} internal={InIp}:{InPort} nat={Nat}",
            s.Pid, s.ExternalIp, s.ExternalPort, s.InternalIp, s.InternalPort, ctx.Request.GetString("nat"));
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Entering the online lobby (FUN_005f0030 sends <c>lobby_id</c> from CMD_GET_LOBBYLIST). The
    /// client only waits for the reply; ERR_LOBBYUSERMAX is the one refusal it knows.
    /// </summary>
    [Command("CMD_JOIN_LOBBY", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> JoinLobby(CommandContext ctx)
    {
        var lobbyId = ctx.Request.GetInt32("lobby_id", 0);
        var lobby = LobbyServices.FirstOrDefault(s => s.Gid == lobbyId) ?? LobbyServices.FirstOrDefault();
        if (lobby is not null && lobby.MaxPlayers > 0 && ctx.Hub.PlayersInBlocks >= lobby.MaxPlayers)
            return Reply.Of(ctx.Fail("ERR_LOBBYUSERMAX"));

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Leaving the online lobby (FUN_005efde0). The player drops out of its room and block, so
    /// the others' lists lose it instead of keeping a ghost entry.
    /// </summary>
    [Command("CMD_LEAVE_LOBBY", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> LeaveLobby(CommandContext ctx)
    {
        var s = ctx.Session;
        if (s.RoomId is { } roomId)
            ctx.Hub.LeaveRoom(s, roomId);

        if (s.BlockId is not null)
        {
            ctx.Hub.PublishBlockPlayerRemoved(s);
            s.BlockId = null;
            s.RoomListWatch = s.BlockPlayerWatch = null;
            if (s.State > SessionState.PlayerSelected)
                s.State = SessionState.PlayerSelected;
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Roster of the room the client asked about (pes2011.exe FUN_006407f0 reads
    /// <c>roomplayer_list[i].pid|room_player_id|room_entry_no|game_entry_no|desiredPosition[0..12]|gameplayer_name</c>,
    /// at most 8 entries).
    /// </summary>
    [Command("CMD_REQWATCHL_ROOMPLAYERLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> WatchRoomPlayers(CommandContext ctx)
    {
        var room = ctx.Hub.FindRoom(ctx.Request.GetInt32("room_id", ctx.Session.RoomId ?? 0));
        ctx.Session.RoomPlayerWatch = Subscription(ctx, "roomplayer_list_num", "roomplayer_list");
        var entries = ctx.Hub.RoomPlayerEntries(room, ctx.Session);
        return Reply.Of(WatchReply(ctx, ctx.Session.RoomPlayerWatch, entries));
    }
}

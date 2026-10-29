// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The in-match flow: who plays which side, the phases, goals, cards, the game-end reports, the
/// rating screen and the results. pes2010.exe sends the same commands with the same fields
/// (FUN_007c1f80, FUN_007c1e00, FUN_0090b2b0) and parses the same replies (FUN_00767df0,
/// FUN_007379a0), so every title is served by them; where PES 2010 differs a command has its own
/// method selected by <see cref="CommandAttribute.Profiles"/>. Function addresses refer to
/// <c>pes2011.exe</c> unless noted.
/// </summary>
public sealed class MatchHandlers(
    GameDbContext db,
    IPlayerRepository players,
    IMatchRepository matches,
    MatchResults matchResults,
    IOptionsMonitor<ServerOptions> options,
    ILogger<MatchHandlers> log)
{
    /// <summary>The titles that share the in-match commands; PES 2010 runs its rooms on the Lobby service.</summary>
    private const GameProfile MatchProfiles = GameProfile.All;
    private const ServiceRole MatchRoles = ServiceRole.Menu | ServiceRole.Lobby | ServiceRole.FdLobby;

    /// <summary>
    /// The host fixes who plays which side (pes2011.exe FUN_0065a7d0 state 3, request
    /// <c>envlist[]{pid,side,sideLeader}</c>). The match is starting; the room moves to GAME
    /// only when the host later reports phase 1ST.
    /// </summary>
    [Command("CMD_SET_GAMEMEMBERENV", Roles = MatchRoles, Profiles = GameProfiles.Modern, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameMemberEnv(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));

        foreach (var entry in ctx.Request.GetList("envlist"))
        {
            var member = room.Snapshot().FirstOrDefault(m => m.Pid == entry.GetInt32("pid", -1));
            if (member is not null)
                member.GameSide = entry.GetString("side") == "AWAY" ? 1 : 0;
        }

        // The room stays in its current state: CMD_CHANGE_GAMEPHASE is what moves it to GAME
        // (ntlserver, the PES 2013 server, does the same and only refreshes the roster here).
        room.MatchStarting = true;
        ctx.Hub.PublishRoomPlayers(room);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// PES 2010's form of <see cref="SetGameMemberEnv"/>: only the owner decides, the whole
    /// envlist is validated before anything changes, and the decision reaches the others through
    /// CMD_WATCH_DECIDE_GAMEPLAYERENV (PES 2010 has no roomplayer list watch).
    /// </summary>
    [Command("CMD_SET_GAMEMEMBERENV", Roles = MatchRoles, Profiles = GameProfile.Pes2010Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameMemberEnvPes2010(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));
        if (ctx.Session.Pid != room.OwnerPid)
            return Reply.Of(ctx.Fail("ERR_NOTROOMOWNER"));

        var entries = ctx.Request.GetList("envlist");
        if ((ctx.Request.Has("list_num") && ctx.Request.GetInt32("list_num", -1) != entries.Count)
            || !room.TrySetGameMemberEnvironment(ctx.Session, entries))
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        // The reply first, then the watch push.
        ctx.Session.Push(ctx.Ok());
        ctx.Hub.PublishDecideGamePlayerEnv(room);
        ctx.Hub.PublishRoomUpdated(room);
        return Reply.None();
    }

    /// <summary>
    /// The match host reports each phase (enum 0x1a: ENTRY, 1ST, BREAK1 ... PK, END). The room
    /// status (enum 0x44) has to follow: in-match FSM FUN_005fa030 ends the match only when a
    /// CMD_WATCH_ROOMSTATE says WAITING, and FUN_005f9ec0 asks for CMD_GET_GAMEADDPOINT on RESULT.
    /// </summary>
    [Command("CMD_CHANGE_GAMEPHASE", Roles = MatchRoles, Profiles = GameProfiles.Modern, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> ChangeGamePhase(CommandContext ctx)
        => ApplyGamePhase(ctx, publishEveryPhase: false);

    /// <summary>
    /// PES 2010 reads the phase from its room-list entry, so every phase is published, including
    /// the ones that keep the room in GAME (1ST -> BREAK1 -> 2ND).
    /// </summary>
    [Command("CMD_CHANGE_GAMEPHASE", Roles = MatchRoles, Profiles = GameProfile.Pes2010Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> ChangeGamePhasePes2010(CommandContext ctx)
        => ApplyGamePhase(ctx, publishEveryPhase: true);

    private async ValueTask<KvMessage[]> ApplyGamePhase(CommandContext ctx, bool publishEveryPhase)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];
        if (!IsMatchHost(ctx, room))
            return [ctx.Ok()];

        var phase = ctx.Request.GetString("phase") ?? "";
        if (phase == "1ST")
        {
            (room.HomeGoals, room.AwayGoals) = (0, 0);
            room.Sheet = new MatchSheet();
            room.ResultRecorded = false;
            room.ForfeitStayerPid = null;
            room.LastResults = new Dictionary<int, PlayerResult>();
        }

        room.Phase = phase;
        if (phase is "EX1ST" or "EX2ND")
            room.Sheet.ReachedExtraTime = true;
        if (phase == "PK")
            room.Sheet.ReachedPenalties = true;

        if (phase == "END")
        {
            // Settled before RESULT goes out: both clients ask CMD_GET_GAMEADDPOINT right after it.
            await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: false);
        }

        var status = phase switch
        {
            "1ST" => "GAME",
            "END" => "RESULT",
            _ => room.Status,
        };
        return await AckThenSetStatus(ctx, room, status, publishEveryPhase);
    }

    /// <summary>The match host's team pick (FUN_005fa030 event 5), kept for the match history.</summary>
    [Command("CMD_SET_GAMETEAM", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameTeam(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));

        room.HomeTeamId = ctx.Request.GetInt32("home_team_id", 0);
        room.AwayTeamId = ctx.Request.GetInt32("away_team_id", 0);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The match host reports each goal (FUN_005fa030 event 3): goal_side HOME|AWAY, the scorer's
    /// goal_gameplayer_id / goal_player_pid and the assist. Kept for the match result.
    /// </summary>
    [Command("CMD_ADD_SCORE", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> AddScore(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));
        if (!IsMatchHost(ctx, room))
            return Reply.Of(ctx.Ok());

        var away = ctx.Request.GetString("goal_side") == "AWAY";
        // Shoot-out goals are kept apart: they decide a draw but are not part of the score.
        if (MatchSheet.PeriodOf(room.Phase) != 4)
        {
            if (away)
                room.AwayGoals++;
            else
                room.HomeGoals++;
        }

        var r = ctx.Request;
        room.Sheet.Goal(away, room.Phase, string.Join('|',
            r.GetInt32("goal_player_pid", 0), r.GetInt32("goal_gameplayer_id", 0), Side(r.GetString("goal_player_side")),
            Side(r.GetString("assist_side")), r.GetInt32("assist_gameplayer_id", 0), Side(r.GetString("assist_player_side"))));

        log.LogInformation("Room {RoomId} goal by pid {Pid} ({Phase}): {Home}-{Away}",
            room.Id, r.GetInt32("goal_player_pid", 0), room.Phase, room.HomeGoals, room.AwayGoals);
        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// A card or an injury (FUN_005fa030): card_pid/card_side/card_gameplayer_id/card_kind
    /// (YELLOW|RED|NONE) and injury_side/injury_gameplayer_id/injury_data. Kept with the match record.
    /// </summary>
    /// <remarks>
    /// The host sends this for <b>every</b> foul, not only for cards: the client always fills
    /// injury_side (the fouled side, always the opposite of card_side) and injury_gameplayer_id
    /// (the fouled player), so neither means somebody got hurt. Both pes2010 and pes2012 captures
    /// carry <c>injury_data={part=0,symptom=0,level=0,level_no_self_conscious=0}</c>,
    /// <c>injury_all=0</c> and <c>injury_one=5</c> on a plain foul; only the nested injury_data
    /// record describes an actual injury.
    /// </remarks>
    [Command("CMD_ADD_FOUL", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> AddFoul(CommandContext ctx)
    {
        if (ctx.CurrentRoom is { } room && IsMatchHost(ctx, room))
        {
            var r = ctx.Request;
            // A community's records count fouls, which only this command reports.
            if (Hub.IsCommunityBlock(room.BlockId))
                room.Sheet.Event(string.Join('|', "FOUL", room.Phase, Side(r.GetString("card_side"))));
            if (r.GetString("card_kind") is { } kind and ("YELLOW" or "RED"))
                room.Sheet.Event(string.Join('|', "CARD", room.Phase, Side(r.GetString("card_side")), kind,
                    r.GetInt32("card_pid", 0), r.GetInt32("card_gameplayer_id", 0)));
            if (Side(r.GetString("injury_side")) is { Length: > 0 } injurySide && IsInjured(r))
            {
                log.LogInformation("Room {RoomId} injury to {Side} ({Phase}), player {Player}: {Data}",
                    room.Id, injurySide, room.Phase, r.GetInt32("injury_gameplayer_id", 0), r.GetValue("injury_data"));
                room.Sheet.Event(string.Join('|', "INJURY", room.Phase, injurySide, r.GetInt32("injury_gameplayer_id", 0)));
            }
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>True when the foul's nested injury_data record actually describes an injury.</summary>
    private static bool IsInjured(KvMessage request)
        => request.GetValue("injury_data") is KvMessage injury
           && (injury.GetInt32("part") != 0
               || injury.GetInt32("symptom") != 0
               || injury.GetInt32("level") != 0
               || injury.GetInt32("level_no_self_conscious") != 0);

    /// <summary>A side as the client names it, or empty: event fields are stored with the match.</summary>
    private static string Side(string? value) => value is "HOME" or "AWAY" ? value : "";

    /// <summary>The host's combination play for the match (total_comb, max_comb); credited to the host.</summary>
    [Command("CMD_UPDATE_COMBINATION", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> UpdateCombination(CommandContext ctx)
    {
        if (ctx.CurrentRoom is { } room && IsMatchHost(ctx, room))
        {
            var (total, max) = (ctx.Request.GetInt32("total_comb", 0), ctx.Request.GetInt32("max_comb", 0));
            room.Sheet.Combination(ctx.Session.Pid, total, max);
            room.TotalCombination = Math.Max(room.TotalCombination, total);
            room.MaxCombination = Math.Max(room.MaxCombination, max);
        }

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Match events (phase, goals, fouls, combinations) count only from the match host — the room
    /// owner, who also sends CMD_SET_GAMEMEMBERENV — and only once that has started the match.
    /// Live captures show the other client never sends them, so anything else is ignored (with
    /// a plain NOERR, which keeps a confused client on its feet) rather than trusted.
    /// </summary>
    private bool IsMatchHost(CommandContext ctx, Room room)
    {
        if (room.OwnerPid == ctx.Session.Pid && room.MatchStarting)
            return true;

        // A community rematch skips CMD_SET_GAMEMEMBERENV: the host sets the room to SETENV, picks
        // teams (CMD_SET_GAMEMEMBER, CMD_SET_GAMETEAM) and kicks off. Its events from SETENV on
        // are the match, and the sides are the ones the first match fixed.
        if (room.OwnerPid == ctx.Session.Pid && Hub.IsCommunityBlock(room.BlockId) && room.Status == "SETENV")
        {
            room.MatchStarting = true;
            return true;
        }

        log.LogWarning("Ignored {Msg} from pid {Pid} in room {RoomId}: not the match host during a match (owner {Owner}, started {Started})",
            ctx.Msg, ctx.Session.Pid, room.Id, room.OwnerPid, room.MatchStarting);
        return false;
    }

    /// <summary>
    /// A player whose peer vanished declares the match forfeited (FUN_005fa030 event 9). Nothing
    /// else will end the match for it: the room goes back to WAITING, which FUN_005fa030 treats
    /// as "match over" and leaves the in-match screen.
    /// </summary>
    [Command("CMD_SET_FORFEITEDGAMEEND", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SetForfeitedGameEnd(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        log.LogInformation("pid {Pid} forfeited the match in room {RoomId}", ctx.Session.Pid, room.Id);

        // The sender is the one left behind. Whether the other quit or the P2P link just broke
        // shows in the next seconds: its connection drops (Hub.LeaveRoom) or it does not.
        if (room.Status == "GAME")
            matchResults.Forfeited(room, ctx.Session.Pid);

        return await AckThenSetStatus(ctx, room, "WAITING");
    }

    /// <summary>
    /// The match host ends the match because a side ran out of players (FUN_005fa030 event 1,
    /// <c>side</c> = the side that lost). Same outcome as phase END: the room shows the result.
    /// Only while a match is being played, so nobody can settle one before it started.
    /// </summary>
    [Command("CMD_SET_INJURYGAMEEND", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SetInjuryGameEnd(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];
        if (room.Status != "GAME" || !IsMatchHost(ctx, room))
            return [ctx.Ok()];

        await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: false);
        return await AckThenSetStatus(ctx, room, "RESULT");
    }

    /// <summary>
    /// The match broke down (FUN_005fa030 events 8/10, <c>is_sync_failed</c> YES|NO). Nothing to
    /// score: the room goes back to WAITING, which takes every client out of the match.
    /// </summary>
    [Command("CMD_SEND_ABNORMALEND", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> SendAbnormalEnd(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        log.LogInformation("pid {Pid} reported an abnormal end in room {RoomId} (sync failed: {Sync})",
            ctx.Session.Pid, room.Id, ctx.Request.GetString("is_sync_failed"));
        if (room.Status == "GAME")
            await matchResults.RecordAsync(room, MatchResults.Lineup(room), leaverPid: null, noMatch: true);

        return await AckThenSetStatus(ctx, room, "WAITING");
    }

    /// <summary>
    /// Asked by every player once the room reaches RESULT (FUN_005f9ec0); FUN_005405c0 reads
    /// <c>player_list[i].pid|addpoint|point|rating|oldrating|vs_count</c>, all optional.
    /// </summary>
    [Command("CMD_GET_GAMEADDPOINT", Roles = MatchRoles, Profiles = MatchProfiles, RequiredState = SessionState.InRoom)]
    public async ValueTask<KvMessage[]> GetGameAddPoint(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        // What MatchResults settled at phase END; a player it has nothing for keeps its values.
        var list = new List<KvMessage>();
        foreach (var member in room.Snapshot())
        {
            if (!room.LastResults.TryGetValue(member.Pid, out var r))
            {
                var player = await players.GetAsync(member.Pid, ctx.CancellationToken);
                r = new PlayerResult(0, player?.Point ?? 0, player?.Rating ?? 0, player?.Rating ?? 0,
                    player?.Stats.MatchCount ?? 0);
            }

            list.Add(new KvMessage()
                .Set("pid", member.Pid)
                .Set("addpoint", r.AddPoint)
                .Set("point", r.Point)
                .Set("rating", r.Rating)
                .Set("oldrating", r.OldRating)
                .Set("vs_count", r.MatchCount));
        }

        return [ctx.Ok().SetList("player_count", "player_list", list)];
    }

    /// <summary>
    /// Match history of <c>pid</c> (FUN_00624790; reply parsed by FUN_00623420). Per entry:
    /// <c>rlist[i].date</c> (unix seconds, FUN_00660320 gmtime), <c>mode</c> (enum 0x25),
    /// <c>ex_type|pk_flag|no_match</c> (YES/NO), <c>score[s].score_1st|_2nd|_ex1st|_ex2nd|_pk</c>
    /// and <c>team[s]</c> for s = HOME, AWAY, then <c>player[k].name|manner|pid|has_guestplayer|
    /// division|rating|side</c> until a name is missing.
    /// </summary>
    [Command("CMD_GET_GAMERESULTS", Roles = MatchRoles, Profiles = MatchProfiles)]
    public async ValueTask<KvMessage[]> GetGameResults(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var entries = new List<KvMessage>();
        foreach (var m in await matches.GetRecentAsync(pid, options.CurrentValue.Rank.HistoryLimit, ctx.CancellationToken))
        {
            var sides = new List<KvMessage>();
            foreach (var (side, who) in new[] { ("HOME", m.HomePid), ("AWAY", m.AwayPid) })
            {
                var p = await players.GetAsync(who, ctx.CancellationToken);
                if (p is not null)
                    sides.Add(new KvMessage()
                        .Set("name", p.Name)
                        .Set("manner", p.Manner)
                        .Set("pid", p.Pid)
                        .Set("has_guestplayer", "NO")
                        .Set("division", RankSystem.Label(p.Division, ctx.Profile))
                        .Set("rating", p.Rating)
                        .Set("side", side));
            }

            // Records from before periods were kept carry the whole score in the first half.
            KvMessage Score(string periods, int total)
            {
                var p = periods.Length > 0 ? MatchSheet.Parse(periods) : [total, 0, 0, 0, 0];
                return new KvMessage()
                    .Set("score_1st", p[0]).Set("score_2nd", p[1]).Set("score_ex1st", p[2])
                    .Set("score_ex2nd", p[3]).Set("score_pk", p[4]);
            }

            // mode is enum 0x25: RANK_MATCH | COMPE_MATCH; every match played here is the former.
            entries.Add(new KvMessage()
                .Set("date", new DateTimeOffset(DateTime.SpecifyKind(m.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds())
                .Set("mode", "RANK_MATCH")
                .Set("ex_type", m.ExtraTime ? "YES" : "NO")
                .Set("pk_flag", m.Penalties ? "YES" : "NO")
                .Set("no_match", m.Disconnected ? "YES" : "NO")
                .Set("score", new[] { Score(m.HomePeriods, m.HomeScore), Score(m.AwayPeriods, m.AwayScore) })
                .Set("team", new KvArray([m.HomeTeamId, m.AwayTeamId]))
                .Set("player", sides));
        }

        return [ctx.Ok().SetList("rlist_num", "rlist", entries)];
    }

    /// <summary>
    /// In-match reports the client only needs acknowledged (FUN_005fa030 and the match-room FSMs):
    /// match info, the reply to a forfeit notice, P2P status and (PES 2011 and later, where only
    /// NOERR is checked) the game member list.
    /// </summary>
    [Command("CMD_SEND_ML_GAMEPLAYERS", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    [Command("CMD_REQ_FORFEITEDGAME_REPLY", Roles = MatchRoles, Profiles = MatchProfiles)]
    [Command("CMD_SEND_P2PSTATUS", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    [Command("CMD_SET_GAMEMEMBER", Roles = MatchRoles, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> MatchAck(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>
    /// The host's possession and shot figures for a community match, kept with the match record
    /// for the community's records and rankings (see <see cref="CommunityStats"/>).
    /// </summary>
    [Command("CMD_SET_COMMU_MATCHINFO", Roles = MatchRoles, Profiles = MatchProfiles)]
    public async ValueTask<KvMessage[]> SetCommunityMatchInfo(CommandContext ctx)
    {
        if (ctx.CurrentRoom is { } room && IsMatchHost(ctx, room))
        {
            var r = ctx.Request;
            var line = string.Join('|', "MATCHINFO",
                r.GetInt32("possession_average_h", 0), r.GetInt32("shot_h", 0), r.GetInt32("suffer_shot_h", 0),
                r.GetInt32("possession_average_a", 0), r.GetInt32("shot_a", 0), r.GetInt32("suffer_shot_a", 0));

            // Sent about a second after the end phase has saved the match: then it goes onto the
            // saved record (not room.MatchId, which is already the next match's), before that onto
            // the sheet that is still to be saved.
            if (room.ResultRecorded && room.SettledMatchId != 0)
                await matchResults.AppendEventAsync(room.SettledMatchId, line);
            else
                room.Sheet.Event(line);
        }

        return [ctx.Ok()];
    }

    /// <summary>PES 2010's host confirms the players; the others learn them through CMD_WATCH_DECIDE_GAMEPLAYER.</summary>
    [Command("CMD_SET_GAMEMEMBER", Roles = MatchRoles, Profiles = GameProfile.Pes2010Pc, RequiredState = SessionState.InRoom)]
    public ValueTask<KvMessage[]> SetGameMemberPes2010(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return Reply.Of(ctx.Fail("ERR_NOTINROOM"));
        if (ctx.Session.Pid != room.OwnerPid)
            return Reply.Of(ctx.Fail("ERR_NOTROOMOWNER"));
        if (!room.MatchStarting)
            return Reply.Of(ctx.Fail("ERR_INVALIDROOMINFO"));

        ctx.Session.Push(ctx.Ok());
        ctx.Hub.PublishDecideGamePlayer(room);
        return Reply.None();
    }

    /// <summary>The reply goes first: the sender's state machine is waiting on it, not on the push.</summary>
    private static ValueTask<KvMessage[]> AckThenSetStatus(CommandContext ctx, Room room, string status, bool publishAnyway = false)
    {
        ctx.Session.Push(ctx.Ok());
        var changed = room.Status != status && ctx.Hub.SetRoomStatus(room, status);
        if (changed || publishAnyway)
            ctx.Hub.PublishRoomUpdated(room);

        return Reply.None();
    }

    /// <summary>
    /// Both players ask for the id as the match is being set up (pes2011.exe FUN_005f9660,
    /// reply parsed by FUN_005f9350 into <c>match_id</c>); it has to be the same for both.
    /// It is the id of the match record, opened here, so it is stable across restarts.
    /// </summary>
    [Command("CMD_GET_MATCHID", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetMatchId(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        if (room is null)
            return [ctx.Fail("ERR_NOTINROOM")];

        return [ctx.Ok().Set("match_id", await matchResults.StartAsync(room))];
    }

    /// <summary>
    /// Minutes each footballer played (player_list: gameplayer_id, play_time, position), sent by
    /// both players once the match has been settled; the room still holds its match id. Each
    /// report is kept as "pid|gameplayer_id|position|minutes" lines, footballers who played only.
    /// </summary>
    [Command("CMD_SET_GAMEPLAYER_PLAYTIME", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public async ValueTask<KvMessage[]> SetGameplayerPlaytime(CommandContext ctx)
    {
        var room = ctx.CurrentRoom;
        var list = ctx.Request.GetList("player_list");
        if (room is not { MatchId: not 0 } || list.Count == 0)
            return [ctx.Ok()];

        var record = await db.Matches.FindAsync([(long)room.MatchId], ctx.CancellationToken);
        if (record is null)
            return [ctx.Ok()];

        var pid = ctx.Session.Pid;
        var lines = list.Where(p => p.GetInt32("play_time", 0) > 0)
            .Select(p => $"{pid}|{p.GetInt32("gameplayer_id", 0)}|{p.GetString("position")}|{p.GetInt32("play_time", 0)}");
        var kept = record.PlayTimes.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith($"{pid}|", StringComparison.Ordinal));
        record.PlayTimes = string.Join('\n', kept.Concat(lines));
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }
}

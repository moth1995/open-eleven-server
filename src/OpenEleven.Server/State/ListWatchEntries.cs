// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>A CMD_REQWATCHL_* registration: the reply has to keep using the names the client chose.</summary>
public sealed record WatchListSubscription(string Msg, int Rqid, string NumName, string ListName);

/// <summary>
/// Entry builders for the PES 2011 and later list watches (room list, room players, lobby players). Field names are the ones the client's
/// parsers read: room entries from <c>FUN_00515770</c>, block players from <c>FUN_00514a80</c>.
/// </summary>
public static class ListWatchEntries
{
    public static KvMessage RoomEntry(Room room, GameProfile profile)
    {
        var env = room.GameEnvironment;
        string Env(string key, string fallback) => env?.GetString(key) ?? fallback;
        int EnvInt(string key, int fallback) => env?.GetInt32(key, fallback) ?? fallback;

        var members = room.Snapshot();
        // enter_no / game_no are the real slot numbers; game_no is -1 until the player enters
        // the match (signed in the client's reader, like game_entry_no below).
        var gamers = members.Select(m =>
        {
            var gamer = new KvMessage()
                .Set("pid", m.Pid)
                .Set("is_room_owner", m.Pid == room.OwnerPid ? "YES" : "NO")
                .Set("side", m.GameSide == 1 ? "AWAY" : "HOME")
                .Set("is_watcher", "NO")
                .Set("mpid", m.Pid)
                .Set("guest_mpid", 0)
                .Set("enter_no", m.RoomEntryNo)
                .Set("game_no", m.GameEntryNo);

            // pes2012.exe parses the room list in "mode 1" (FUN_006023e0 -> FUN_00540fe0), where
            // FUN_0053d650 reads each gamer's profile inline instead of looking the pid up in the
            // block roster (FUN_0053bbf0 does that lookup only in mode 0). A missing key drops the
            // whole room row.
            if (profile.IsSince2012())
                gamer.Set("name", m.PlayerName)
                    .Set("xuid", 0)
                    .Set("division", m.Card.Division)
                    .Set("is_restricted", "NO")
                    .Set("lang", m.Card.Lang)
                    .Set("rating", m.Card.Rating)
                    .Set("selfreport_level", m.Card.SelfReportLevel)
                    .Set("position_want", m.Card.PositionWant);

            return gamer;
        }).ToArray();

        var entry = new KvMessage()
            .Set("match_type", room.MatchType.Length > 0 ? room.MatchType : "OC_FREE")
            .Set("room_id", room.Id)
            .Set("status", room.Status)
            .Set("game_phase", room.Phase)
            .Set("name", room.Name)
            // FUN_00515770 drops the whole row unless room_list[i].gameenv is present.
            .Set("gameenv", new KvMessage()
                .Set("cpuLevel", Env("cpuLevel", "NORMAL"))
                .Set("gametime", Env("gametime", "10MINUTES"))
                .Set("injury", Env("injury", "NO"))
                .Set("condition", Env("condition", "NORMAL"))
                .Set("ball_type", EnvInt("ball_type", 0))
                .Set("exGame", Env("exGame", "NO"))
                .Set("pkOnOff", Env("pkOnOff", "NO"))
                .Set("substitution", EnvInt("substitution", 3))
                .Set("limitTime", Env("limitTime", "NOSET")))
            .Set("is_nogame", "NO")
            .Set("is_passwd", room.Password.Length > 0 ? "YES" : "NO")
            .Set("max_players", room.MaxMembers)
            .Set("is_invite_limit", room.InviteLimited ? "YES" : "NO")
            .Set("team_name", room.TeamName)
            .Set("short_teamname", room.ShortTeamName)
            .Set("is_legends_vs_player", room.LegendsVsPlayer)
            .Set("total_combination", room.TotalCombination)
            .Set("max_combination", room.MaxCombination)
            .Set("dlcontent", room.DlcVersion.Length > 0 ? room.DlcVersion : "1.00")
            .SetList("gamer_num", "gamer", gamers)
            .Set("enable_guest", room.AllowGuest ? "YES" : "NO");

        // pes2012.exe's room key table ends with FD_rank right after is_legends_vs_player; PES 2011 has no such key.
        // ponytail: always 0, Legends ranking is not served. Real value once it is.
        // FUN_0053BB50 / FUN_0053E650 / FUN_0053E740 read team[0..1], team_flag[0..2] and score[0..1]
        // as paths into arrays (FUN_00d39600). They must go out as arrays: the client's tokenizer
        // (FUN_00d39470) splits on '[', so a literal team[0]= key fails the parse of the whole message.
        if (profile.IsSince2012())
        {
            var score = new KvMessage()
                .Set("score_1st", 0).Set("score_2nd", 0).Set("score_ex1st", 0)
                .Set("score_ex2nd", 0).Set("score_pk", 0);
            return entry
                .Set("team_flag", room.TeamFlag)
                .Set("score", new[] { score, score })
                .Set("FD_rank", 0)
                .Set("team", new KvArray(new object?[] { room.HomeTeamId, room.AwayTeamId }));
        }

        return entry.Set("team_flag", room.TeamFlag);
    }

    /// <summary>
    /// One member of a room as the watcher sees it. FUN_00640e10 reads pid + ex_ip/ex_port/in_ip/in_port
    /// out of every entry to build the P2P peer table (it skips the viewer's own pid); without
    /// them the client never opens a connection to the host and gives up with CMD_LEAVE_ROOM after 40 s.
    /// </summary>
    public static KvMessage RoomPlayerEntry(Session member, PeerEndpoints at, GameProfile profile)
    {
        var entry = new KvMessage()
            .Set("pid", member.Pid)
            .Set("room_player_id", member.Pid)
            .Set("room_entry_no", member.RoomEntryNo)
            .Set("game_entry_no", member.GameEntryNo)
            .Set("gameplayer_name", member.PlayerName)
            .Set("ex_ip", at.ExternalIp)
            .Set("ex_port", at.ExternalPort)
            .Set("in_ip", at.InternalIp)
            .Set("in_port", at.InternalPort);

        if (profile.IsSince2012())
        {
            // FUN_0069C0B0 reads desiredPosition[0..13] (14 slots, a path into the array) and the
            // user-list screen also reads the profile fields below.
            return entry
                .Set("desiredPosition", KvArray.Repeat("NO", 14))
                .Set("gameplayer_id", 0)
                .Set("mpid", member.Pid)
                .Set("guest_mpid", 0)
                .Set("xuid", 0)
                .Set("division", member.Card.Division)
                .Set("rating", member.Card.Rating)
                .Set("is_restricted", "NO")
                .Set("fd_selfreport_level", member.Card.SelfReportLevel)
                .Set("fd_position_want", member.Card.PositionWant)
                .Set("player_name", member.PlayerName)
                .Set("lang", member.Card.Lang);
        }

        return entry.Set("desiredPosition", KvArray.Repeat("NO", 13));
    }

    /// <summary>Makes a PES 2010 style block entry acceptable to the 2011 parser.</summary>
    public static KvMessage BlockPlayerEntry(KvMessage e, GameProfile profile)
    {
        foreach (var (key, value) in new (string, object)[]
                 {
                     ("npid", ""), ("xuid", 0), ("manner", 3), ("selfreport_level", "PRO"),
                     ("beginnermark", "NO"), ("position_want", "CF"),
                 })
        {
            if (!e.Has(key))
                e.Set(key, value);
        }

        if (!e.Has("desired_position"))
            e.Set("desired_position", KvArray.Repeat("NO", 13));

        // pes2012.exe FUN_00544b40 reads blockplayer_list[i] through a key table that needs three
        // more keys than PES 2011; an entry missing any of them is skipped (the count still shows).
        if (profile.IsSince2012())
        {
            e.Set("is_restricted", "NO");
            e.Set("player_name", e.GetString("name") ?? "");
            e.Set("gameplayer_id", 0);
        }

        return e;
    }
}

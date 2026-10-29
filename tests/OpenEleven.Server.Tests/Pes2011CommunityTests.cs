// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2011CommunityTests
{
    private static Task<ServerHarness> StartAsync() =>
        ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");

    [Fact]
    public async Task A_created_community_is_listed_for_its_owner_and_found_by_a_search()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");

        var created = Assert.Single(await Create(harness, owner, "my test Community", password: "secret"));
        var id = created.GetInt32("commu_id");
        Assert.True(id > 0);

        var joined = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_JOINEDCOMMULIST"));
        Assert.Equal(1, joined.GetInt32("commu_num"));
        var mine = Assert.Single(joined.GetList("clist"));
        Assert.Equal((id, "my test Community", 1, "secret"),
            (mine.GetInt32("commu_id"), mine.GetString("commu_name"), mine.GetInt32("member_count"), mine.GetString("password")));
        Assert.Equal("owner", Assert.Single(mine.GetList("owner_info")).GetString("name"));
        Assert.Equal(7, ((KvArray)mine.GetValue("week")!).Values.Count(v => v is "YES"));

        // Someone else is told only that a password is needed.
        var found = Assert.Single(await Search(harness, other, "Community"));
        Assert.Equal(1, found.GetInt32("total_found_num"));
        Assert.Equal("*", Assert.Single(found.GetList("mlist")).GetString("password"));

        Assert.Equal(0, Assert.Single(await Search(harness, other, "nothing like it")).GetInt32("commu_num"));
    }

    [Fact]
    public async Task Joining_needs_the_password_and_ends_in_the_joined_list()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club", password: "secret")).GetInt32("commu_id");

        Join(await Dispatch(harness, other, id, "wrong"), "ERR_PASSWDINCORRECT");
        Join(await Dispatch(harness, other, id, "secret"), "NOERR");
        Join(await Dispatch(harness, other, id, "secret"), "ERR_ALREADYINCOMMU");
        Join(await Dispatch(harness, other, 999, ""), "ERR_COMMUNOTEXIST");

        var list = Assert.Single(await harness.DispatchAsync(other, "CMD_GET_JOINEDCOMMULIST"));
        Assert.Equal(2, Assert.Single(list.GetList("clist")).GetInt32("member_count"));
    }

    [Fact]
    public async Task A_player_belongs_to_at_most_five_communities()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        for (var i = 0; i < 5; i++)
            Assert.Equal("NOERR", Assert.Single(await Create(harness, owner, $"Club {i}")).GetString("result"));

        Assert.Equal("ERR_TOOMANYCOMMU", Assert.Single(await Create(harness, owner, "Sixth")).GetString("result"));
        Assert.Equal(5, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_JOINEDCOMMULIST")).GetInt32("commu_num"));
    }

    [Fact]
    public async Task The_online_member_ranking_wraps_each_community_in_commu_info()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        await Create(harness, owner, "Club");

        var ranking = Assert.Single(await harness.DispatchAsync(owner, "CMD_SEARCH_COMMU_BYRANKING", withFields: r => r
            .Set("ranking_kind", "LOGINMEMBER_NUM")));
        var entry = Assert.Single(ranking.GetList("mlist"));
        Assert.Equal(1, entry.GetInt32("value"));
        Assert.Equal("Club", ((KvMessage)entry.GetValue("commu_info")!).GetString("commu_name"));
    }

    [Fact]
    public async Task The_creator_can_make_it_current_and_open_its_member_list()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var outsider = await PlayerAsync(harness, "outsider");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");

        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id))).GetString("result"));
        Assert.Equal("ERR_NOTJOINEDCOMMU", Assert.Single(await harness.DispatchAsync(outsider, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id))).GetString("result"));

        var members = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_MEMBERLIST", withFields: r => r
            .Set("commu_id", id).Set("listnum_name", "member_list_num").Set("list_name", "member_list")));
        Assert.Equal(1, members.GetInt32("member_list_num"));
        var entry = Assert.Single(members.GetList("member_list"));
        Assert.Equal(("Club", "COMMUNITY", "owner"),
            (entry.GetString("commu_name"), entry.GetString("mode"), Assert.Single(entry.GetList("info")).GetString("name")));
    }

    [Fact]
    public async Task The_owner_manages_the_current_community_and_others_cannot()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, other, id, ""), "NOERR");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        await harness.DispatchAsync(other, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));

        var info = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_INFO", withFields: r => r.Set("commu_id", id))).GetList("commu_info"));
        Assert.Equal(("Club", 2), (info.GetString("commu_name"), info.GetInt32("member_count")));

        Assert.Equal("ERR_NOTCOMMUOWNER", Result(await harness.DispatchAsync(other, "CMD_SET_COMMU_OPTION", withFields: r => r.Set("commu_name", "Mine"))));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_SET_COMMU_OPTION", withFields: r => r
            .Set("commu_name", "Renamed").Set("commu_description", "d").Set("password", "pw").Set("invite_only", "NO")
            .Set("week", new KvArray("YES", "NO", "NO", "NO", "NO", "NO", "NO")).Set("start_time", 0).Set("end_time", 0))));
        var renamed = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_INFO", withFields: r => r.Set("commu_id", id))).GetList("commu_info"));
        Assert.Equal(("Renamed", "pw", 1), (renamed.GetString("commu_name"), renamed.GetString("password"), ((KvArray)renamed.GetValue("week")!).Values.Count(v => v is "YES")));

        Assert.Equal("ERR_NOTCOMMUOWNER", Result(await harness.DispatchAsync(other, "CMD_DEL_COMMU_MEMBER", withFields: r => r.Set("commu_id", id).Set("pid", owner.Pid))));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_DEL_COMMU_MEMBER", withFields: r => r.Set("commu_id", id).Set("pid", other.Pid))));
        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_JOINEDCOMMULIST")).GetList("clist").Single().GetInt32("member_count"));

        Assert.Equal("ERR_DATABASE", Result(await harness.DispatchAsync(owner, "CMD_WITHDRAW_COMMU", withFields: r => r.Set("commu_id", id))));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_DEL_COMMU")));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_JOINEDCOMMULIST")).GetInt32("commu_num"));
    }

    [Fact]
    public async Task A_member_can_leave_and_a_community_without_matches_has_empty_records()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, other, id, ""), "NOERR");
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(other, "CMD_WITHDRAW_COMMU", withFields: r => r.Set("commu_id", id))));
        Assert.Equal("ERR_PLAYERNOTINCOMMU", Result(await harness.DispatchAsync(other, "CMD_WITHDRAW_COMMU", withFields: r => r.Set("commu_id", id))));

        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAMLIST")).GetInt32("list_num", -1));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_PASTLEAGUELIST")).GetInt32("list_num"));
        var ranking = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_MEMBERRANKING", withFields: r => r.Set("ranking_type", "USER_RANK_CNT_MATCH")));
        Assert.Equal((0, true), (ranking.GetInt32("data_num", -1), ranking.Has("date")));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAMRANKING")).GetInt32("data_num", -1));
        var stats = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_MEMBERSRESULTS", withFields: r => r.Set("pid", owner.Pid))).GetList("stats"));
        Assert.Equal(0, Assert.Single(stats.GetList("score_get")).GetInt32("total_count", -1));
    }

    [Fact]
    public async Task Records_and_rankings_are_worked_out_from_the_community_matches()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, other, id, ""), "NOERR");

        // owner (team 5) beats other (team 7) 3-1, then a 2-2 draw; a match of another community is ignored.
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var first = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            db.Matches.AddRange(
                Match(id, owner.Pid, other.Pid, 5, 7, 3, 1, first,
                    "GOAL|1ST|HOME|1|10|HOME|HOME|11|HOME\nGOAL|1ST|HOME|1|10|HOME||0|\nCARD|2ND|AWAY|YELLOW|2|20\nFOUL|2ND|AWAY\nFOUL|2ND|AWAY\nMATCHINFO|60|8|4|40|4|8"),
                Match(id, owner.Pid, other.Pid, 5, 7, 2, 2, first.AddHours(1), ""),
                Match(id + 1, owner.Pid, other.Pid, 5, 7, 9, 0, first.AddHours(2), ""));
            await db.SaveChangesAsync();
        }

        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var mine = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_MEMBERSRESULTS", withFields: r => r.Set("pid", owner.Pid))).GetList("stats"));
        Assert.Equal((2, 1, 0, 1, 50, 60, 1, 1), (mine.GetInt32("match_count"), mine.GetInt32("win_count"), mine.GetInt32("lose_count"),
            mine.GetInt32("draw_count"), mine.GetInt32("win_average"), mine.GetInt32("possession_average"),
            mine.GetInt32("continuous_win_max"), mine.GetInt32("continuous_win_current", -1) == 0 ? 1 : 0));
        var goals = Assert.Single(mine.GetList("score_get"));
        AssertShape(goals, ("total_count", 'n'), ("average", 'n'));
        Assert.Equal((5, 3), (goals.GetInt32("total_count"), goals.GetInt32("average")));
        Assert.Equal(1, Assert.Single(mine.GetList("assist")).GetInt32("total_count"));
        Assert.Equal(8, Assert.Single(mine.GetList("shot")).GetInt32("total_count"));
        var theirs = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_MEMBERSRESULTS", withFields: r => r.Set("pid", other.Pid))).GetList("stats"));
        Assert.Equal((1, 2, 1), (theirs.GetInt32("lose_count"), Assert.Single(theirs.GetList("foul")).GetInt32("total_count"), Assert.Single(theirs.GetList("yellow_card")).GetInt32("total_count")));

        var wins = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_MEMBERRANKING", withFields: r => r.Set("ranking_type", "USER_RANK_CNT_WIN")));
        Assert.Equal(2, wins.GetInt32("data_num"));
        var top = wins.GetList("ranking_list")[0];
        AssertShape(top, ("disp_rank", 'n'), ("value", 'n'), ("member_info", 'l'));
        Assert.Equal((1, 1, "owner"), (top.GetInt32("disp_rank"), top.GetInt32("value"), Assert.Single(top.GetList("member_info")).GetString("name")));
        AssertShape(Assert.Single(top.GetList("member_info")), PlayerBlockShape);
        Assert.Equal(2, wins.GetList("ranking_list")[1].GetInt32("disp_rank") == 2 ? 2 : 0);

        var teams = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAMLIST"));
        Assert.Equal(new object?[] { new KvRaw("5"), new KvRaw("7") }.Length, ((KvArray)teams.GetValue("team_list")!).Values.Count);
        Assert.Equal(2, teams.GetInt32("list_num"));
        var teamRank = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAMRANKING", withFields: r => r.Set("ranking_type", "TEAM_RANK_CNT_WIN")));
        AssertShape(teamRank.GetList("ranking_list")[0], ("team_id", 'n'), ("disp_rank", 'n'), ("value", 'n'));
        Assert.Equal((5, 1), (teamRank.GetList("ranking_list")[0].GetInt32("team_id"), teamRank.GetList("ranking_list")[0].GetInt32("value")));
        var team = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAMRESULTS", withFields: r => r.Set("team_id", 7))).GetList("stats"));
        Assert.Equal((2, 1, 1), (team.GetInt32("match_count"), team.GetInt32("lose_count"), team.GetInt32("draw_count")));

        // The client reads a team record with the player-record parser (FUN_00555ff0): a missing field fails it.
        string[] required =
        [
            "match_count", "win_count", "lose_count", "draw_count", "win_average", "possession_average", "league_win_count",
            "cup_win_count", "continuous_win_current", "continuous_win_max", "continuous_lose_current", "continuous_lose_max",
            "best_combination_max", "total_combination_max", "score_get", "score_lost", "assist", "foul", "yellow_card",
            "red_card", "shot", "suffer_shot",
        ];
        Assert.Empty(required.Except(team.Fields.Select(f => f.Key)));
    }

    [Fact]
    public async Task Member_list_entries_each_get_their_own_slot_index()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");

        var list = Assert.Single(await harness.DispatchAsync(member, "CMD_REQWATCHL_COMMU_MEMBERLIST", withFields: r => r.Set("commu_id", id))).GetList("member_list");
        Assert.Equal([0, 1], list.Select(e => Assert.Single(e.GetList("info")).GetInt32("index")));
    }

    [Fact]
    public async Task Open_member_lists_are_updated_when_members_join_enter_and_leave()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_MEMBERLIST", rqid: 70, withFields: r => r
            .Set("commu_id", id).Set("listnum_name", "member_list_num").Set("list_name", "member_list"));
        Drain(owner);

        // A new member is added to the open list, under the watch's rqid.
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        var added = Assert.Single(Drain(owner));
        Assert.Equal((70, "ADD", "member"), (added.Rqid, added.GetString("list_type"), Assert.Single(Assert.Single(added.GetList("member_list")).GetList("info")).GetString("name")));

        // Opening the community moves the member's entry to COMMUNITY, with a slot of its own.
        await harness.DispatchAsync(member, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var opened = Assert.Single(Assert.Single(Drain(owner)).GetList("member_list"));
        Assert.Equal(("COMMUNITY", 1), (opened.GetString("mode"), Assert.Single(opened.GetList("info")).GetInt32("index")));

        // The league list works the same way.
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
        await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_COMPEMEMBERLIST", rqid: 71, withFields: r => r
            .Set("commu_id", id).Set("compe_id", leagueId).Set("listnum_name", "member_list_num").Set("list_name", "member_list"));
        Drain(owner);

        await harness.DispatchAsync(member, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
        // The community list shows it as well: the league details screen counts its participants there.
        var pushes = Drain(owner);
        var joined = Assert.Single(pushes, m => m.Rqid == 71);
        var inLeague = Assert.Single(joined.GetList("member_list"));
        Assert.Equal((71, "COMMUNITY_COMPE", leagueId), (joined.Rqid, inLeague.GetString("mode"), inLeague.GetInt32("compe_id")));
        var inCommunity = Assert.Single(Assert.Single(pushes, m => m.Rqid == 70).GetList("member_list"));
        Assert.Equal(("COMMUNITY_COMPE", leagueId), (inCommunity.GetString("mode"), inCommunity.GetInt32("compe_id")));

        await harness.DispatchAsync(member, "CMD_LEAVE_COMMU_LEAGUE");
        pushes = Drain(owner);
        Assert.Equal("COMMUNITY", Assert.Single(Assert.Single(pushes, m => m.Rqid == 71).GetList("member_list")).GetString("mode"));
        Assert.Equal("COMMUNITY", Assert.Single(Assert.Single(pushes, m => m.Rqid == 70).GetList("member_list")).GetString("mode"));

        // A member withdrawing is a DEL on the others' open list.
        await harness.DispatchAsync(member, "CMD_WITHDRAW_COMMU", withFields: r => r.Set("commu_id", id));
        var removed = Assert.Single(Drain(owner), m => m.Rqid == 70);
        Assert.Equal("DEL", removed.GetString("list_type"));
    }

    [Fact]
    public async Task League_member_list_marks_the_members_who_have_the_league_open()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));

        var list = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_COMPEMEMBERLIST", withFields: r => r
            .Set("commu_id", id).Set("compe_id", leagueId))).GetList("member_list");
        Assert.All(list, e => Assert.Equal(("COMMUNITY_COMPE", leagueId), (e.GetString("mode"), e.GetInt32("compe_id"))));
    }

    [Fact]
    public async Task A_league_match_is_put_up_answered_played_and_lands_in_the_tables()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        foreach (var who in new[] { owner, member })
        {
            await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
            await harness.DispatchAsync(who, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0));
        }

        // Each player's squad, as the client saves it: counters at zero.
        KvMessage Footballer(int gameplayer) => new KvMessage().Set("gameplayer_id", gameplayer).Set("goal", 0).Set("assist", 0)
            .Set("uniform_number", 1).Set("now_yellow_card_count", 0).Set("now_red_card_count", 0)
            .Set("total_yellow_card", 0).Set("total_red_card", 0).Set("injury_all", 0).Set("injury_one", 0).Set("injury_data", "");
        await harness.DispatchAsync(owner, "CMD_SET_COMMU_TEAM", withFields: r => r.Set("team_id", 101).Set("member_num", 2)
            .Set("member", new List<KvMessage> { Footballer(1001), Footballer(5786) }));
        await harness.DispatchAsync(member, "CMD_SET_COMMU_TEAM", withFields: r => r.Set("team_id", 107).Set("member_num", 1)
            .Set("member", new List<KvMessage> { Footballer(2002) }));

        // Two cells, home and away: two fixtures. The playable list carries the id and both cells.
        var cellShape = new (string, char)[] { ("cell_id", 'n'), ("team_id", 'n'), ("entry_user_count", 'n'), ("entry_user_info", 'l') };
        var playable = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST", withFields: r => r
            .Set("listnum_name", "match_list_num").Set("list_name", "match_list")));
        Assert.Equal(2, playable.GetInt32("match_list_num"));
        var fixture = playable.GetList("match_list")[0];
        AssertShape(fixture, ("match_id", 'n'), ("home_team_info", 'l'), ("away_team_info", 'l'));
        AssertShape(Assert.Single(fixture.GetList("home_team_info")), cellShape);
        var matchId = fixture.GetInt32("match_id");

        // The member listens; the owner puts the fixture up in a room of the community.
        await harness.DispatchAsync(member, "CMD_WATCH_COMMU_MATCH", rqid: 40);
        await harness.DispatchAsync(owner, "CMD_WATCH_COMMU_MATCH_WAITING", rqid: 41);
        var room = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Room owner").Set("match_type", "COMMU").Set("max_players", 2))).GetInt32("room_id");
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_APPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("room_id", room))));

        var offered = Assert.Single(Drain(member));
        Assert.Equal((40, leagueId, matchId, owner.Pid, room), (offered.Rqid, offered.GetInt32("compe_id"), offered.GetInt32("match_id"), offered.GetInt32("from_pid"), offered.GetInt32("room_id")));

        var answer = await harness.DispatchAsync(member, "CMD_REPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("reply", "YES"));
        Assert.Equal("NOERR", answer[0].GetString("result"));
        var waiting = Assert.Single(Drain(owner).Where(m => m.MsgName == "CMD_WATCH_COMMU_MATCH_WAITING"));
        Assert.Equal((41, 2), (waiting.Rqid, waiting.GetInt32("player_num")));
        foreach (var listed in waiting.GetList("player_list"))
            AssertShape(listed, PlayerBlockShape);
        Assert.Equal(new[] { owner.Pid, member.Pid }, waiting.GetList("player_list").Select(p => p.GetInt32("pid")));

        // The one who said yes gets the same list, under rqid 1 since it has no watch parked.
        var mirrored = Assert.Single(answer, m => m.MsgName == "CMD_WATCH_COMMU_MATCH_WAITING");
        Assert.Equal((1, 2), (mirrored.Rqid, mirrored.GetInt32("player_num")));

        // The room plays 2-1 and the score goes to the league, then into every table.
        harness.Hub.JoinRoom(member, room, "");
        owner.GameSide = 0;
        member.GameSide = 1;
        var played = harness.Hub.FindRoom(room)!;
        played.HomeGoals = 2;
        played.AwayGoals = 1;
        // Home (the owner) scores with 1001 from an assist by 5786; the member is booked.
        played.Sheet.Event($"GOAL|1ST|HOME|{owner.Pid}|1001|HOME|HOME|5786|HOME");
        played.Sheet.Event($"CARD|2ND|AWAY|YELLOW|{member.Pid}|2002");
        var matchResults = harness.Services.GetRequiredService<MatchResults>();
        played.MatchStarting = true;
        var recordId = await matchResults.StartAsync(played);
        await matchResults.RecordAsync(played, MatchResults.Lineup(played), null, false);

        // The clients report possession and shots a moment after the end phase has saved the match,
        // by when the room is back in the lobby and has already asked for the next match's id:
        // only the host's report counts, and it lands on the saved record (once), not that one.
        played.MatchId = 0;
        Assert.NotEqual(recordId, await matchResults.StartAsync(played));
        foreach (var who in new[] { member, owner, owner })
            await harness.DispatchAsync(who, "CMD_SET_COMMU_MATCHINFO", withFields: r => r
                .Set("possession_average_h", 4500).Set("shot_h", 11).Set("suffer_shot_h", 8)
                .Set("possession_average_a", 5500).Set("shot_a", 8).Set("suffer_shot_a", 11));
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var saved = await scope.ServiceProvider.GetRequiredService<GameDbContext>().Matches.FindAsync((long)recordId);
            Assert.Single(saved!.Events.Split('\n'), l => l == "MATCHINFO|4500|11|8|5500|8|11");
        }

        // The squads now carry what the matches gave them: the goal and assist, and the booking.
        var entered = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUEENTRYCELLINFO")).GetList("clist");
        int CellOf(Session who) => entered.Single(c => c.GetList("entry_user_info").Any(u => u.GetInt32("pid") == who.Pid)).GetInt32("cell_id");
        var ownerSquad = Assert.Single(await harness.DispatchAsync(member, "CMD_GET_COMMU_TEAM", withFields: r => r.Set("cell_id", CellOf(owner)))).GetList("member");
        Assert.Equal((1, 0, 0), (ownerSquad.Single(m => m.GetInt32("gameplayer_id") == 1001).GetInt32("goal"),
            ownerSquad.Single(m => m.GetInt32("gameplayer_id") == 1001).GetInt32("assist"), ownerSquad.Single(m => m.GetInt32("gameplayer_id") == 5786).GetInt32("goal")));
        Assert.Equal(1, ownerSquad.Single(m => m.GetInt32("gameplayer_id") == 5786).GetInt32("assist"));
        var memberSquad = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAM", withFields: r => r.Set("cell_id", CellOf(member)))).GetList("member");
        Assert.Equal(1, Assert.Single(memberSquad).GetInt32("total_yellow_card"));

        var list = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")).GetList("compe_list"));
        Assert.Equal((2, 1), (list.GetInt32("total_match_count"), list.GetInt32("finished_match_count")));

        var matches = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_MATCHLIST", withFields: r => r.Set("compe_id", leagueId)));
        var done = matches.GetList("match_list").Single(m => m.GetString("match_status") == "END");
        AssertShape(done, ("match_status", 's'), ("match", 'l'), ("home_goal", 'l'), ("away_goal", 'l'));
        var score = Assert.Single(done.GetList("home_goal"));
        AssertShape(score, ("score_1st", 'n'), ("score_2nd", 'n'), ("score_ex1st", 'n'), ("score_ex2nd", 'n'), ("score_pk", 'n'));

        var results = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_COMPERESULT"));
        Assert.Equal(2, results.GetInt32("info_num"));
        var first = results.GetList("mlist").Single(m => m.GetValue("played") is true);
        AssertShape(first, ("played", 's'), ("home_id", 'n'), ("home_pts", 'n'), ("away_id", 'n'), ("away_pts", 'n'));
        Assert.Equal((2, 1), (first.GetInt32("home_pts"), first.GetInt32("away_pts")));

        var table = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_COMPERANKING"));
        Assert.Equal(2, table.GetInt32("info_num"));
        var top = table.GetList("i_list")[0];
        AssertShape(top, ("team_rank_disp", 'n'), ("team_rank_point", 'n'), ("team_rank_win", 'n'), ("team_rank_draw", 'n'), ("team_rank_lose", 'n'),
            ("team_rank_score", 'n'), ("team_rank_lost", 'n'), ("team_rank_scorediff", 'n'), ("entry_info", 'l'));
        Assert.Equal((1, 3, 1, 2, 1), (top.GetInt32("team_rank_disp"), top.GetInt32("team_rank_point"), top.GetInt32("team_rank_win"), top.GetInt32("team_rank_score"), top.GetInt32("team_rank_scorediff")));

        var goals = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_GOALRANKING", withFields: r => r.Set("compe_id", leagueId)));
        var scorer = goals.GetList("goal_ranking")[0];
        AssertShape(scorer, ("disp_rank", 'n'), ("ga_count", 'n'), ("entry_info", 'l'));
        Assert.Equal((1, 2), (scorer.GetInt32("disp_rank"), scorer.GetInt32("ga_count")));

        // Ending the league keeps its table as a past result.
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_END_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId))));
        var past = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_PASTLEAGUERESULT", withFields: r => r.Set("compe_id", leagueId)));
        Assert.Equal(2, past.GetInt32("info_num"));
    }

    [Fact]
    public async Task A_cancelled_offer_and_a_refusal_reach_the_applicants_waiting_watch()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        foreach (var who in new[] { owner, member })
        {
            await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
            await harness.DispatchAsync(who, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0));
        }

        var matchId = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST")).GetList("match_list")[0].GetInt32("match_id");
        var room = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Room owner").Set("match_type", "COMMU").Set("max_players", 2))).GetInt32("room_id");

        // The opponent is not in the community: nobody to offer the match to.
        await harness.DispatchAsync(member, "CMD_RESET_CURRENT_COMMU");
        Assert.Equal("ERR_COMMUMATCHREFUSED", Result(await harness.DispatchAsync(owner, "CMD_APPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("room_id", room))));

        // Back in the community without any watch open: the offer still reaches them, under rqid 1.
        await harness.DispatchAsync(member, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        Drain(member);
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_APPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("room_id", room))));
        var unsolicited = Assert.Single(Drain(member));
        Assert.Equal((1, matchId, room), (unsolicited.Rqid, unsolicited.GetInt32("match_id"), unsolicited.GetInt32("room_id")));
        await harness.DispatchAsync(owner, "CMD_CANCEL_COMMU_MATCH", withFields: r => r.Set("match_id", matchId));
        Drain(member);

        await harness.DispatchAsync(member, "CMD_WATCH_COMMU_MATCH", rqid: 40);
        await harness.DispatchAsync(owner, "CMD_WATCH_COMMU_MATCH_WAITING", rqid: 41);
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_APPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("room_id", room))));
        Drain(member);

        Assert.Equal("NOERR", Result(await harness.DispatchAsync(member, "CMD_REPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("reply", "NO"))));
        // The waiting watch's only error is the cancel one, so a refusal is told as that.
        Assert.Equal("ERR_COMMUMATCHCANCELED", Assert.Single(Drain(owner).Where(m => m.MsgName == "CMD_WATCH_COMMU_MATCH_WAITING")).GetString("result"));

        Assert.Equal("ERR_PLAYERISNOTGAMER", Result(await harness.DispatchAsync(member, "CMD_CANCEL_COMMU_MATCH", withFields: r => r.Set("match_id", matchId))));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_CANCEL_COMMU_MATCH", withFields: r => r.Set("match_id", matchId))));
        Assert.Equal("ERR_MATCHNOTEXIST", Result(await harness.DispatchAsync(member, "CMD_REPLY_COMMU_MATCH", withFields: r => r.Set("match_id", matchId).Set("reply", "YES"))));
    }

    [Fact]
    public async Task Head_to_head_over_http_answers_with_the_reply_text_the_client_takes_as_its_own()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, other, id, ""), "NOERR");

        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var first = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        db.Matches.AddRange(
            Match(id, owner.Pid, other.Pid, 5, 7, 3, 1, first, "MATCHINFO|60|8|4|40|4|8"),
            Match(id, other.Pid, owner.Pid, 7, 5, 2, 2, first.AddHours(1), ""));
        await db.SaveChangesAsync();

        // The url ends in "?", and the client may add one of its own.
        var reply = new KvReader().Parse(await CommunityVersus.MembersAsync(db, $"??commu_id={id}&base_pid={owner.Pid}&oppo_pid={other.Pid}", default));
        Assert.Equal("NOERR", reply.GetString("result"));
        var stats = Assert.Single(reply.GetList("stats"));
        AssertShape(stats, ("match_count", 'n'), ("win_count", 'n'), ("lose_count", 'n'), ("draw_count", 'n'), ("win_average", 'n'), ("possession_average", 'n'),
            ("score_get", 'l'), ("score_lost", 'l'), ("shot", 'l'), ("suffer_shot", 'l'));
        Assert.Equal((2, 1, 0, 1, 50), (stats.GetInt32("match_count"), stats.GetInt32("win_count"), stats.GetInt32("lose_count"), stats.GetInt32("draw_count"), stats.GetInt32("win_average")));
        Assert.Equal(5, Assert.Single(stats.GetList("score_get")).GetInt32("total_count"));

        var teams = new KvReader().Parse(await CommunityVersus.TeamsAsync(db, $"commu_id={id}&base_team_id=7&oppo_team_id=5", default));
        Assert.Equal((2, 0, 1), (Assert.Single(teams.GetList("stats")).GetInt32("match_count"), Assert.Single(teams.GetList("stats")).GetInt32("win_count"), Assert.Single(teams.GetList("stats")).GetInt32("draw_count")));

        Assert.StartsWith("result=\"ERR_COMMUNOTEXIST\"", await CommunityVersus.MembersAsync(db, "commu_id=999&base_pid=1&oppo_pid=2", default));
        Assert.StartsWith("result=\"ERR_PLAYERNOTINCOMMU\"", await CommunityVersus.MembersAsync(db, $"commu_id={id}&base_pid={owner.Pid}&oppo_pid=424242", default));

        // The urls are advertised.
        var urls = Assert.Single(await harness.DispatchAsync(harness.NewSession(ServiceRole.Gate, SessionState.Connected), "CMD_GET_URLLIST"));
        Assert.Equal(2, urls.GetList("ulist").Count(u => u.GetString("type") is "COMMU_MEMBERS_VS" or "COMMU_TEAM_VS"));
    }

    private static MatchRecord Match(int commuId, int home, int away, int homeTeam, int awayTeam, int homeScore, int awayScore, DateTime at, string events)
        => new()
        {
            CommuId = commuId, HomePid = home, AwayPid = away, HomeTeamId = homeTeam, AwayTeamId = awayTeam,
            HomeScore = homeScore, AwayScore = awayScore, PlayedAt = at, Events = events.Replace("\\n", "\n"), Completed = true,
        };

    [Fact]
    public async Task Community_chat_reaches_the_other_members_but_not_outsiders()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var outsider = await PlayerAsync(harness, "outsider");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));

        await harness.DispatchAsync(owner, "CMD_SEND_TEXTCHAT", withFields: r => r
            .Set("channel", "COMMUNITY").Set("statement", "hello club").Set("list_flg", "CHAT_BAN").Set("list_pid", new KvArray(new object?[] { 0 })));

        Assert.Equal("hello club", Assert.Single(Drain(member)).GetString("statement"));
        Assert.Empty(Drain(outsider));
    }

    [Fact]
    public async Task The_community_screen_counts_completed_matches_and_finished_leagues()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var other = await PlayerAsync(harness, "other");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, other, id, ""), "NOERR");
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            var at = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            var unfinished = Match(id, owner.Pid, other.Pid, 5, 7, 0, 0, at, "");
            unfinished.Completed = false;
            db.Matches.AddRange(Match(id, owner.Pid, other.Pid, 5, 7, 3, 1, at, ""), Match(id, owner.Pid, other.Pid, 5, 7, 2, 2, at, ""),
                unfinished, Match(id + 1, owner.Pid, other.Pid, 5, 7, 9, 0, at, ""));
            await db.SaveChangesAsync();
        }

        var info = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_INFO", withFields: r => r.Set("commu_id", id))).GetList("commu_info"));
        Assert.Equal((2, 0), (info.GetInt32("total_match_count"), info.GetInt32("total_compe_count")));

        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        await harness.DispatchAsync(owner, "CMD_END_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
        info = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_INFO", withFields: r => r.Set("commu_id", id))).GetList("commu_info"));
        Assert.Equal(1, info.GetInt32("total_compe_count"));
    }

    [Fact]
    public async Task A_community_rematch_is_tracked_without_a_new_game_member_env()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var roomId = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Room owner").Set("match_type", "COMMU").Set("max_players", 2))).GetInt32("room_id");
        var room = harness.Hub.FindRoom(roomId)!;

        // The host puts the room in SETENV for the rematch and kicks off: no CMD_SET_GAMEMEMBERENV.
        room.TrySetStatus("SETENV");
        await harness.DispatchAsync(owner, "CMD_CHANGE_GAMEPHASE", withFields: r => r.Set("phase", "1ST"));

        Assert.Equal(("1ST", "GAME"), (room.Phase, room.Status));
    }

    [Fact]
    public async Task The_playable_list_holds_only_the_matches_of_the_league_that_is_open()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));

        // Two leagues, both players entered in each.
        var leagues = new List<int>();
        foreach (var name in new[] { "First", "Second" })
        {
            var leagueId = Assert.Single(await League(harness, owner, name)).GetInt32("compe_id");
            leagues.Add(leagueId);
            foreach (var who in new[] { owner, member })
            {
                await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
                await harness.DispatchAsync(who, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0));
            }
        }

        async Task<int[]> Playable(Session who) => Assert.Single(await harness.DispatchAsync(who, "CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST"))
            .GetList("match_list").Select(m => m.GetInt32("match_id")).ToArray();

        // The second league is the one open: its (home and away) fixtures only.
        Assert.Equal(2, (await Playable(owner)).Length);
        await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagues[0]));
        var first = await Playable(owner);
        Assert.Equal(2, first.Length);
        await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagues[1]));
        Assert.Empty((await Playable(owner)).Intersect(first));
    }

    [Fact]
    public async Task League_chat_reaches_the_league_participants_as_a_league_message()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var bystander = await PlayerAsync(harness, "bystander");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        foreach (var who in new[] { member, bystander })
            Join(await Dispatch(harness, who, id, ""), "NOERR");
        foreach (var who in new[] { owner, member, bystander })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));

        await harness.DispatchAsync(owner, "CMD_SEND_TEXTCHAT", withFields: r => r
            .Set("channel", "COMMUNITY_LEAGUE").Set("statement", "hello cup").Set("list_flg", "CHAT_BAN").Set("list_pid", new KvArray(new object?[] { 0 })));

        var heard = Assert.Single(Drain(member));
        Assert.Equal(("COMMUNITY_LEAGUE", "hello cup"), (heard.GetString("channel"), heard.GetString("statement")));
        Assert.Empty(Drain(bystander));
    }

    [Fact]
    public async Task The_game_plan_is_saved_and_read_back_verbatim()
    {
        await using var harness = await StartAsync();
        var player = await PlayerAsync(harness, "owner");
        // Nothing saved yet: the client dies on ERR_DATANOTFOUND, so it gets the default plan.
        var fresh = Assert.Single(await harness.DispatchAsync(player, "CMD_GET_GAMEPLAN"));
        Assert.Equal((DefaultGamePlan.Size, DefaultGamePlan.Size * 2), (fresh.GetInt32("gameplan_data_size"), fresh.GetString("gameplan_data")!.Length));

        var plan = string.Concat(Enumerable.Range(0, 1060).Select(i => (i % 256).ToString("X2")));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(player, "CMD_SET_GAMEPLAN", withFields: r => r
            .Set("gameplan_data_size", 1060).Set("gameplan_data", plan))));
        Assert.Equal("ERR_DATABASE", Result(await harness.DispatchAsync(player, "CMD_SET_GAMEPLAN", withFields: r => r
            .Set("gameplan_data_size", 10).Set("gameplan_data", "00"))));

        var read = Assert.Single(await harness.DispatchAsync(player, "CMD_GET_GAMEPLAN"));
        Assert.Equal((1060, plan), (read.GetInt32("gameplan_data_size"), read.GetString("gameplan_data")));
    }

    [Fact]
    public async Task A_member_opens_leagues_in_the_current_community_up_to_three()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var outsider = await PlayerAsync(harness, "outsider");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");

        Assert.Equal("ERR_COMMUNOTEXIST", Result(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")));
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")).GetInt32("compe_num", -1));

        for (var i = 0; i < 3; i++)
            Assert.True(Assert.Single(await League(harness, owner, $"League {i}")).GetInt32("compe_id") > 0);
        Assert.Equal("ERR_COMMUCOMPEISFULL", Result(await League(harness, owner, "Fourth")));

        var list = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST"));
        Assert.Equal(3, list.GetInt32("compe_num"));
        var first = list.GetList("compe_list")[0];
        Assert.Equal("owner", Assert.Single(first.GetList("compe_chairman_info")).GetString("name"));
        var settings = Assert.Single(first.GetList("env_setting"));
        Assert.Equal(("League 0", 0, 1, 8, 1), (settings.GetString("compe_name"), settings.GetInt32("match_time", -1), settings.GetInt32("injury", -1), settings.GetInt32("ball_type", -1), settings.GetInt32("home_and_away", -1)));

        var leagueId = first.GetInt32("compe_id");
        Assert.Equal("YES", Assert.Single(await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId))).GetString("is_registered"));
        Assert.Equal("YES", Assert.Single(await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId))).GetString("is_registered"));
        Assert.Equal("ERR_COMMUCOMPENOTEXIST", Result(await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", 999))));

        // Someone outside the community has none open, so cannot open one.
        Assert.Equal("ERR_COMMUNOTEXIST", Result(await League(harness, outsider, "Mine")));
    }

    // Entries are read through the client's own key tables, where a number read as text (or the
    // other way round) rejects the whole list; so each entry type is pinned key by key.
    private static void AssertShape(KvMessage entry, params (string Key, char Kind)[] keys)
    {
        foreach (var (key, kind) in keys)
        {
            Assert.True(entry.Has(key), $"missing {key}");
            var value = entry.GetValue(key);
            switch (kind)
            {
                case 'n': Assert.True(value is int or long or uint or KvRaw, $"{key} must be a number, was {value?.GetType().Name}"); break;
                case 's': Assert.True(value is string or bool, $"{key} must be text, was {value?.GetType().Name}"); break;
                case 'l': Assert.True(value is IReadOnlyList<KvMessage>, $"{key} must be a list, was {value?.GetType().Name}"); break;
            }
        }
    }

    private static readonly (string, char)[] PlayerBlockShape =
        [("pid", 'n'), ("name", 's'), ("manner", 'n'), ("division", 's'), ("rating", 'n'), ("status", 's'), ("lang", 's'), ("index", 'n'), ("npid", 's')];

    [Fact]
    public async Task League_cells_are_entered_listed_and_left_with_the_shapes_the_client_reads()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        foreach (var who in new[] { owner, member })
            await harness.DispatchAsync(who, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));

        // Each player gets a cell of their own (cell_id 0): the client's cell holds a single player,
        // so the owner's cell is full for the member. Asking again returns the cell already held.
        var cell = Assert.Single(await harness.DispatchAsync(owner, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0))).GetInt32("joined_cell_id");
        Assert.True(cell > 0);
        Assert.Equal(cell, Assert.Single(await harness.DispatchAsync(owner, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0))).GetInt32("joined_cell_id"));
        Assert.Equal("ERR_ENTRYISFULL", Result(await harness.DispatchAsync(member, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", cell))));
        var memberCell = Assert.Single(await harness.DispatchAsync(member, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0))).GetInt32("joined_cell_id");
        Assert.NotEqual(cell, memberCell);

        var info = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUEENTRYCELLINFO"));
        Assert.Equal(2, info.GetInt32("cell_info_num"));
        var entry = info.GetList("clist")[0];
        AssertShape(entry, [("cell_id", 'n'), ("team_id", 'n'), ("entry_user_count", 'n'), ("entry_user_info", 'l')]);
        Assert.All(info.GetList("clist"), e => Assert.Equal(1, e.GetInt32("entry_user_count")));
        foreach (var user in entry.GetList("entry_user_info"))
            AssertShape(user, PlayerBlockShape);

        // The league list carries the same cells, counted.
        var league = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")).GetList("compe_list"));
        Assert.Equal((2, 2), (league.GetInt32("entered_cell_count"), league.GetList("entry_info").Count));

        // The client sends CMD_LEAVE_COMMU_LEAGUE when the player backs out of the league menu: it
        // closes the league on that player's side and keeps their entry, cell and team.
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(member, "CMD_LEAVE_COMMU_LEAGUE")));
        Assert.Equal(2, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUEENTRYCELLINFO")).GetInt32("cell_info_num"));
        // With no league open the command has its one error.
        Assert.Equal("ERR_PLAYERNOTINCOMPE", Result(await harness.DispatchAsync(member, "CMD_LEAVE_COMMU_LEAGUE")));

        // Coming back finds the same cell; removing members is the owner's job, and a cell goes with its player.
        await harness.DispatchAsync(member, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_DEL_COMMU_LEAGUEMEMBER", withFields: r => r.Set("pid", member.Pid))));
        Assert.Equal("ERR_NOTCOMMUOWNER", Result(await harness.DispatchAsync(member, "CMD_DEL_COMMU_LEAGUEMEMBER", withFields: r => r.Set("pid", owner.Pid))));
        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUEENTRYCELLINFO")).GetInt32("cell_info_num"));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_DEL_COMMU_LEAGUEMEMBER", withFields: r => r.Set("pid", owner.Pid))));
        Assert.Equal(0, Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")).GetList("compe_list")).GetInt32("entered_cell_count", -1));
    }

    [Fact]
    public async Task A_cells_team_is_kept_as_sent_and_a_finished_league_is_listed_as_past()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));

        Assert.Equal("ERR_COMMUCOMPENOTEXIST", Result(await harness.DispatchAsync(owner, "CMD_SET_COMMU_TEAM", withFields: r => r.Set("team_id", 5).Set("member_num", 0))));
        var cell = Assert.Single(await harness.DispatchAsync(owner, "CMD_ENTRY_COMMU_LEAGUECELL", withFields: r => r.Set("cell_id", 0))).GetInt32("joined_cell_id");

        var footballer = new KvMessage().Set("gameplayer_id", 1234).Set("goal", 2).Set("assist", 1).Set("uniform_number", 9)
            .Set("now_yellow_card_count", 0).Set("now_red_card_count", 0).Set("total_yellow_card", 3).Set("total_red_card", 0)
            .Set("injury_all", 0).Set("injury_one", 0).Set("injury_data", "");
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_SET_COMMU_TEAM", withFields: r => r
            .Set("team_id", 5).Set("member_num", 1).Set("member", new List<KvMessage> { footballer }))));

        var team = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_TEAM", withFields: r => r.Set("cell_id", cell)));
        Assert.Equal((5, cell, 1), (team.GetInt32("team_id"), team.GetInt32("cell_id"), team.GetInt32("member_num")));
        var back = Assert.Single(team.GetList("member"));
        Assert.Equal((1234, 2, 9, 3), (back.GetInt32("gameplayer_id"), back.GetInt32("goal"), back.GetInt32("uniform_number"), back.GetInt32("total_yellow_card")));

        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_PASTLEAGUELIST")).GetInt32("list_num", -1));
        Assert.Equal("NOERR", Result(await harness.DispatchAsync(owner, "CMD_END_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId))));
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_LEAGUELIST")).GetInt32("compe_num", -1));
        var past = Assert.Single(await harness.DispatchAsync(owner, "CMD_GET_COMMU_PASTLEAGUELIST"));
        Assert.Equal(1, past.GetInt32("list_num"));
        var finished = Assert.Single(past.GetList("rlist"));
        AssertShape(finished, [("compe_id", 'n'), ("compe_name", 's'), ("opening_date", 'n'), ("finish_date", 'n'), ("user_count", 'n'), ("compe_chairman_info", 'l')]);
        AssertShape(Assert.Single(finished.GetList("compe_chairman_info")), PlayerBlockShape);
    }

    [Fact]
    public async Task The_league_member_list_and_the_community_member_list_share_one_entry_shape()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var leagueId = Assert.Single(await League(harness, owner, "Cup")).GetInt32("compe_id");
        await harness.DispatchAsync(owner, "CMD_JOIN_COMMU_LEAGUE", withFields: r => r.Set("compe_id", leagueId));

        var shape = new (string, char)[]
        {
            ("mode", 's'), ("lobby_name", 's'), ("block_name", 's'), ("commu_name", 's'), ("commu_id", 'n'), ("room_id", 'n'),
            ("beginnermark", 's'), ("position_want", 's'), ("selfreport_level", 's'), ("info", 'l'),
        };
        var community = Assert.Single(Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_MEMBERLIST", withFields: r => r.Set("commu_id", id))).GetList("member_list"));
        AssertShape(community, shape);
        var league = Assert.Single(await harness.DispatchAsync(owner, "CMD_REQWATCHL_COMMU_COMPEMEMBERLIST", withFields: r => r.Set("commu_id", id).Set("compe_id", leagueId)));
        AssertShape(Assert.Single(league.GetList("member_list")), shape);
        AssertShape(Assert.Single(Assert.Single(league.GetList("member_list")).GetList("info")), PlayerBlockShape);
    }

    private static Task<IReadOnlyList<KvMessage>> League(ServerHarness harness, Session who, string name)
        => harness.DispatchAsync(who, "CMD_CREATE_COMMU_LEAGUE", withFields: r => r
            .Set("compe_name", name).Set("match_time", "TIME_5").Set("injury", "YES").Set("ball_type", 8)
            .Set("home_and_away", "YES").Set("comment", "a comment"));

    [Fact]
    public async Task A_community_room_is_made_without_a_lobby_and_listed_only_to_that_community()
    {
        await using var harness = await StartAsync();
        var owner = await PlayerAsync(harness, "owner");
        var member = await PlayerAsync(harness, "member");
        var outsider = await PlayerAsync(harness, "outsider");
        var id = Assert.Single(await Create(harness, owner, "Club")).GetInt32("commu_id");
        Join(await Dispatch(harness, member, id, ""), "NOERR");
        var rooms = new Action<KvMessage>(r => r.Set("listnum_name", "room_list_num").Set("list_name", "room_list"));

        await harness.DispatchAsync(owner, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        var created = Assert.Single(await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Room owner").Set("match_type", "COMMU").Set("max_players", 4)));
        Assert.Equal("NOERR", created.GetString("result"));

        await harness.DispatchAsync(member, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id));
        Assert.Single(Assert.Single(await harness.DispatchAsync(member, "CMD_REQWATCHL_ROOMLIST", withFields: rooms)).GetList("room_list"));

        // Not a member: no community to open, and the room is not in its (empty) list.
        Assert.Equal("ERR_NOTJOINEDCOMMU", Result(await harness.DispatchAsync(outsider, "CMD_SET_CURRENT_COMMU", withFields: r => r.Set("commu_id", id))));

        // Leaving the community takes the member out of its block again.
        await harness.DispatchAsync(member, "CMD_RESET_CURRENT_COMMU");
        Assert.Equal((null, SessionState.PlayerSelected), (member.BlockId, member.State));
    }

    private static List<KvMessage> Drain(Session s)
    {
        var pushed = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                pushed.Add(m);
        return pushed;
    }

    private static string? Result(IReadOnlyList<KvMessage> reply) => Assert.Single(reply).GetString("result");

    private static Task<IReadOnlyList<KvMessage>> Create(ServerHarness harness, Session who, string name, string password = "")
        => harness.DispatchAsync(who, "CMD_CREATE_COMMU", withFields: r => r
            .Set("commu_name", name).Set("commu_description", "some comment").Set("password", password)
            .Set("invite_only", "NO").Set("week", new KvArray(Enumerable.Repeat("YES", 7).ToArray()))
            .Set("start_time", 0).Set("end_time", 0));

    private static Task<IReadOnlyList<KvMessage>> Search(ServerHarness harness, Session who, string keyword)
        => harness.DispatchAsync(who, "CMD_SEARCH_COMMU", withFields: r => r
            .Set("keyword", keyword).Set("target", "NAME").Set("lang", "ANY").Set("has_friends", "NO")
            .Set("target_pid", 0).Set("week", new KvArray(Enumerable.Repeat("NO", 7).ToArray()))
            .Set("active_time", "HOUR_NONE").Set("index", 0));

    private static Task<IReadOnlyList<KvMessage>> Dispatch(ServerHarness harness, Session who, int id, string password)
        => harness.DispatchAsync(who, "CMD_JOIN_COMMU", withFields: r => r.Set("commu_id", id).Set("password", password));

    private static void Join(IReadOnlyList<KvMessage> reply, string result)
        => Assert.Equal(result, Assert.Single(reply).GetString("result"));

    private static async Task<Session> PlayerAsync(ServerHarness harness, string name)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var account = new Account { GameId = name, PasswordHash = "x" };
        var player = new Player { Name = name, NormalizedName = name.ToUpperInvariant(), Division = Division.D5 };
        account.Players.Add(player);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var session = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        session.Pid = player.Pid;
        session.PlayerName = name;
        session.AccountId = account.Id;
        return session;
    }
}

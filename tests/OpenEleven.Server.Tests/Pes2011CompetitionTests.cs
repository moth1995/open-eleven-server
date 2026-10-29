// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2011CompetitionTests
{
    private static Task<ServerHarness> HarnessWithCup()
    {
        var now = DateTime.UtcNow;
        string At(double days) => now.AddDays(days).ToString("O", CultureInfo.InvariantCulture);
        return ServerHarness.CreateAsync(s =>
        {
            s["Server:GameProfile"] = "Pes2011Pc";
            s["Server:Competitions:0:Id"] = "7";
            s["Server:Competitions:0:Name"] = "Open Cup";
            s["Server:Competitions:0:EntryStart"] = At(-1);
            s["Server:Competitions:0:EntryEnd"] = At(2);
            s["Server:Competitions:0:MatchStart"] = At(3);
            s["Server:Competitions:0:MatchEnd"] = At(5);
            s["Server:Competitions:0:End"] = At(6);
            s["Server:Competitions:0:DlcVersion"] = "2.00";
        });
    }

    private static Session Player(ServerHarness harness)
    {
        var s = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        s.Pid = 1;
        return s;
    }

    [Fact]
    public async Task Running_cup_is_listed_with_every_key_the_client_requires()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);

        var reply = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "NOW_PLAY").Set("compe_kind", "KONAMI_CUP_ONLINE").Set("compe_mode", "NORMAL")));

        Assert.Equal(("NOERR", "NOW_PLAY", 1), (reply.GetString("result"), reply.GetString("compe_list_kind"), reply.GetInt32("compe_num")));
        var cup = Assert.Single(reply.GetList("compe_list"));
        foreach (var key in new[]
                 {
                     "compe_kind", "compe_id", "entry_start_time", "entry_end_time", "match_start_time",
                     "match_end_time", "compe_end_time", "compe_name", "status", "category", "entry_limit",
                     "entry_limit_rank", "dlcver", "player_capacity", "player_num", "champion_notice",
                     "guest_count", "is_coach_mode", "need_konamiid", "entry_limit_division",
                 })
            Assert.True(cup.Has(key), key);
        Assert.Equal(("ENTRY", "FALSE", "2.00"), (cup.GetString("status"), cup.GetString("champion_notice"), cup.GetString("dlcver")));

        var schedule = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_LIST", withFields: r => r
            .Set("compe_list_kind", "SCHEDULE")));
        Assert.Equal(0, schedule.GetInt32("compe_num"));
    }

    [Fact]
    public async Task Qualifying_round_screens_answer_with_every_key_the_client_parsers_require()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);
        Assert.Equal("ERR_COMPE_NOTENTRY", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYRESULTS")).GetString("result"));
        await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7));

        Assert.Equal(1, Assert.Single(await harness.DispatchAsync(s, "CMD_REQWATCH_COMPLG_PLAYERNUM")).GetInt32("player_num"));

        // The cup's matches start in 3 days: waiting, with the time to wait for.
        var start = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MATCH_START_TIME"));
        Assert.Equal("TRUE", start.GetString("is_waiting_time"));
        Assert.True(start.Has("match_start_time"));

        // Rankings before any match: the standings list the entrant, the scorer rankings no one (FUN_00562e60, FUN_005620a0, FUN_00562760).
        foreach (var (command, extra) in new[]
                 {
                     ("CMD_GET_COMPLG_PRE_RESULTS", new[] { "compe_name", "date" }),
                     ("CMD_GET_COMPLG_GOALRANKING", []),
                     ("CMD_GET_COMPLG_ASSISTRANKING", []),
                 })
        {
            var ranking = Assert.Single(await harness.DispatchAsync(s, command, withFields: r => r
                .Set("compe_id", 7).Set("start_rank", 1).Set("get_count", 20).Set("get_pid", 0)));
            var rows = command == "CMD_GET_COMPLG_PRE_RESULTS" ? 1 : 0;
            Assert.Equal(("NOERR", rows, 1, rows), (ranking.GetString("result"), ranking.GetInt32("rank_num"), ranking.GetInt32("start_rank"), ranking.GetInt32("rank_count")));
            foreach (var key in extra.Append("rank_list"))
                Assert.True(ranking.Has(key), $"{command} {key}");
        }

        // My results: the team name must not be empty, whatever the match list holds.
        var mine = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYRESULTS"));
        Assert.Equal(("NOERR", "-", 0), (mine.GetString("result"), mine.GetString("team_name"), mine.GetInt32("match_result_num")));

        Assert.Equal("ERR_COMPE_NOT_SELECT_TEAM", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_TEAM_DATA", withFields: r => r.Set("pid", 1))).GetString("result"));

        // Picking a team: it is kept, played back, counted in the popularity ranking and named in my results.
        var squad = new List<KvMessage> { new KvMessage().Set("gameplayer_id", 4072).Set("uniform_number", 1), new KvMessage().Set("gameplayer_id", 8639).Set("uniform_number", 3) };
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_SET_COMPLG_TEAM", withFields: r => r
            .Set("team_id", 108).Set("team_name", "FC BARCELONA").Set("member_num", 2).Set("member", squad))).GetString("result"));
        var data = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_TEAM_DATA", withFields: r => r.Set("pid", 1)));
        Assert.Equal(("FC BARCELONA", 108, 2), (data.GetString("team_name"), data.GetInt32("team_id"), data.GetInt32("member_num")));
        Assert.True(data.Has("pid") && data.Has("name")); // required by FUN_005640f0
        Assert.Equal(new[] { 4072, 8639 }, data.GetList("member").Select(m => m.GetInt32("gameplayer_id")));
        var popular = Assert.Single(Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_TEAMRANKING", withFields: r => r.Set("compe_id", 7))).GetList("rank_list"));
        Assert.Equal((108, 1), (popular.GetInt32("team_id"), popular.GetInt32("user_num")));
        Assert.Equal("FC BARCELONA", Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYRESULTS")).GetString("team_name"));
        Assert.Equal("YES", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7))).GetString("is_select_team"));

        // The exit icon (CMD_LEAVE_COMPLG) only ends the visit: the entry and team are there next time.
        Assert.Equal("NOERR", Assert.Single(await harness.DispatchAsync(s, "CMD_LEAVE_COMPLG")).GetString("result"));
        Assert.Equal(7, Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYENTRY_COMPEID")).GetInt32("compe_id"));
        Assert.Equal("YES", Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7))).GetString("is_select_team"));
    }

    [Fact]
    public async Task A_played_competition_match_fills_the_standings_rankings_results_and_player_results()
    {
        await using var harness = await HarnessWithCup();
        var home = await EntrantAsync(harness, "ana", 108, "FC BARCELONA", 4072);
        var away = await EntrantAsync(harness, "bob", 105, "REAL MADRID", 8639);
        var at = DateTime.UtcNow;
        await using (var scope = harness.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
            // ana 3-1 bob: two of her goals by gameplayer 4072 (one assisted by 4073), one own goal of bob's side
            // that is nobody's, a yellow for bob's 8639. Another competition's match is ignored.
            db.Matches.AddRange(
                new MatchRecord
                {
                    CompeId = 7, Completed = true, HomePid = home.Pid, AwayPid = away.Pid, HomeTeamId = 108, AwayTeamId = 105,
                    HomeScore = 3, AwayScore = 1, HomePeriods = "2,1,0,0,0", AwayPeriods = "1,0,0,0,0", PlayedAt = at,
                    Events = "GOAL|1ST|HOME|1|4072|HOME|HOME|4073|HOME\nGOAL|1ST|HOME|1|4072|HOME||0|\nGOAL|2ND|HOME|2|8639|AWAY||0|\nGOAL|1ST|AWAY|2|8639|AWAY||0|\nCARD|2ND|AWAY|YELLOW|2|8639",
                },
                new MatchRecord { CompeId = 8, Completed = true, HomePid = home.Pid, AwayPid = away.Pid, HomeScore = 9, PlayedAt = at });
            await db.SaveChangesAsync();
        }

        var table = Assert.Single(await harness.DispatchAsync(home, "CMD_GET_COMPLG_PRE_RESULTS", withFields: r => r
            .Set("compe_id", 7).Set("start_rank", 1).Set("get_count", 100)));
        Assert.Equal((2, 2), (table.GetInt32("rank_num"), table.GetInt32("rank_count")));
        var rows = table.GetList("rank_list");
        Assert.Equal(("ana", 1, 3, 1, 0, 0, 3, 1, 2, "FC BARCELONA"),
            (rows[0].GetString("name"), rows[0].GetInt32("rank"), rows[0].GetInt32("point"), rows[0].GetInt32("win_count"), rows[0].GetInt32("lose_count"),
                rows[0].GetInt32("draw_count"), rows[0].GetInt32("score"), rows[0].GetInt32("loss"), rows[0].GetInt32("scorediff"), rows[0].GetString("team_name")));
        Assert.Equal(("bob", 2, 1), (rows[1].GetString("name"), rows[1].GetInt32("rank"), rows[1].GetInt32("lose_count")));
        foreach (var key in new[] { "rank_by_team", "team_id", "pid", "update_date", "rating", "ml_point", "continuous_win", "npid", "is_qualifier" })
            Assert.True(rows[0].Has(key), key);

        var goals = Assert.Single(await harness.DispatchAsync(home, "CMD_GET_COMPLG_GOALRANKING", withFields: r => r.Set("compe_id", 7).Set("start_rank", 1).Set("get_count", 20)));
        Assert.Equal(("ana", 3, 1), (goals.GetList("rank_list")[0].GetString("name"), goals.GetList("rank_list")[0].GetInt32("score"), goals.GetList("rank_list")[0].GetInt32("rank")));
        var assists = Assert.Single(await harness.DispatchAsync(home, "CMD_GET_COMPLG_ASSISTRANKING", withFields: r => r.Set("compe_id", 7).Set("start_rank", 1).Set("get_count", 20)));
        Assert.Equal(("ana", 1), (assists.GetList("rank_list")[0].GetString("name"), assists.GetList("rank_list")[0].GetInt32("assist")));

        var mine = Assert.Single(await harness.DispatchAsync(away, "CMD_GET_COMPLG_MYRESULTS"));
        Assert.Equal((1, 2), (mine.GetInt32("match_result_num"), mine.GetInt32("my_rank")));
        var match = Assert.Single(mine.GetList("match_result_list"));
        var side = Assert.IsType<KvMessage>(match.GetValue("home"));
        Assert.Equal(("ana", "FC BARCELONA", 2, 1), (side.GetString("name"), side.GetString("team_name"),
            Assert.IsType<KvMessage>(side.GetValue("score")).GetInt32("score_1st"), Assert.IsType<KvMessage>(side.GetValue("score")).GetInt32("score_2nd")));
        foreach (var key in new[] { "pid", "manner", "rating", "team_id", "division", "is_guest_player", "npid" })
            Assert.True(side.Has(key), key);

        // Player results: ana's footballers carry what they did in this competition.
        var squad = Assert.Single(await harness.DispatchAsync(away, "CMD_GET_COMPLG_TEAM_DATA", withFields: r => r.Set("pid", home.Pid)));
        var scorer = squad.GetList("member").Single(m => m.GetInt32("gameplayer_id") == 4072);
        Assert.Equal((2, 0, 0, 0), (scorer.GetInt32("goal"), scorer.GetInt32("assist"), scorer.GetInt32("total_yellow_card"), scorer.GetInt32("total_red_card")));
        Assert.Equal(1, squad.GetList("member").Single(m => m.GetInt32("gameplayer_id") == 4073).GetInt32("assist"));
        var booked = Assert.Single(await harness.DispatchAsync(home, "CMD_GET_COMPLG_TEAM_DATA", withFields: r => r.Set("pid", away.Pid)));
        Assert.Equal((1, 1), (booked.GetList("member").Single(m => m.GetInt32("gameplayer_id") == 8639).GetInt32("goal"), booked.GetList("member").Single(m => m.GetInt32("gameplayer_id") == 8639).GetInt32("total_yellow_card")));
    }

    private static async Task<Session> EntrantAsync(ServerHarness harness, string name, int teamId, string team, int footballer)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GameDbContext>();
        var account = new Account { GameId = name, PasswordHash = "x" };
        var squad = new List<KvMessage> { new KvMessage().Set("gameplayer_id", footballer).Set("uniform_number", 1) };
        if (name == "ana")
            squad.Add(new KvMessage().Set("gameplayer_id", 4073).Set("uniform_number", 2));
        var player = new Player
        {
            Name = name, NormalizedName = name.ToUpperInvariant(), Division = Division.D5, CompetitionId = 7,
            CompetitionTeamId = teamId, CompetitionTeamName = team,
            CompetitionTeam = new KvWriter().Write(new KvMessage().Set("member", squad)).TrimEnd('\0'),
        };
        account.Players.Add(player);
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var session = harness.NewSession(ServiceRole.Menu, SessionState.PlayerSelected);
        session.Pid = player.Pid;
        session.PlayerName = name;
        session.AccountId = account.Id;
        return session;
    }

    [Fact]
    public async Task Entered_competitions_stay_counted_in_the_player_info_after_leaving()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);

        async Task<KvMessage?> CompeLine()
        {
            var info = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_PLAYERINFO", withFields: r => r.Set("pid", 1)));
            return info.GetList("compe_list").SingleOrDefault();
        }

        Assert.Null(await CompeLine());

        // Joining twice is one entry; quitting takes the entry away but not the count.
        await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7));
        await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7));
        await harness.DispatchAsync(s, "CMD_QUIT_COMPLG");
        var line = await CompeLine();
        Assert.NotNull(line);
        Assert.Equal(("Open Cup", "KONAMI_CUP_ONLINE", "NORMAL", 1), (line!.GetString("compe_name"), line.GetString("compe_kind"), line.GetString("mode"), line.GetInt32("join_num")));
    }

    [Fact]
    public async Task Joining_shows_as_my_entry_and_counts_until_left()
    {
        await using var harness = await HarnessWithCup();
        var s = Player(harness);

        // The client drops a join reply without is_select_team as a failed join (FUN_0055f140), joining again included.
        for (var i = 0; i < 2; i++)
        {
            var joined = Assert.Single(await harness.DispatchAsync(s, "CMD_JOIN_COMPLG", withFields: r => r.Set("compe_id", 7)));
            Assert.Equal(("NOERR", "NO"), (joined.GetString("result"), joined.GetString("is_select_team")));
        }

        var ranking = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_TEAMRANKING", withFields: r => r.Set("compe_id", 7)));
        Assert.Equal(("NOERR", 0), (ranking.GetString("result"), ranking.GetInt32("rank_list_num")));
        Assert.Equal(7, Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYENTRY_COMPEID")).GetInt32("compe_id"));

        var info = Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_INFO", withFields: r => r.Set("compe_id", 7)));
        var data = Assert.IsType<KvMessage>(info.GetValue("compe_data"));
        Assert.Equal((7, 1), (data.GetInt32("compe_id"), data.GetInt32("player_num")));

        await harness.DispatchAsync(s, "CMD_QUIT_COMPLG");
        Assert.Equal(0, Assert.Single(await harness.DispatchAsync(s, "CMD_GET_COMPLG_MYENTRY_COMPEID")).GetInt32("compe_id"));
        Assert.Equal("ERR_COMPE_NOTENTRY", Assert.Single(await harness.DispatchAsync(s, "CMD_QUIT_COMPLG")).GetString("result"));
    }
}

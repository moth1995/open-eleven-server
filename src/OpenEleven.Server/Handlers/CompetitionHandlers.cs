// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Competitions (KONAMI CUP ONLINE): browsing, the detail page, entry and withdrawal.
/// Competitions come from config; a player's entry is <c>Players.CompetitionId</c>.
/// </summary>
/// <remarks>
/// The client parsers drop a whole reply when one required key is missing or one enum name is
/// unknown, so every key below is read by them (list FUN_0055cdb0, detail FUN_0055daf0, kinds
/// FUN_00565da0, my entry FUN_0055c9d0). Times are Unix seconds.
/// </remarks>
public sealed class CompetitionHandlers(
    GameDbContext db,
    IPlayerRepository players,
    IOptionsMonitor<ServerOptions> options,
    TimeProvider time)
{
    private const ServiceRole Roles = ServiceRole.Menu;

    /// <summary>FUN_0055cdb0 rejects compe_num above 20.</summary>
    private const int MaxListed = 20;

    /// <summary>FUN_005687a0 rejects match_result_num above 20.</summary>
    private const int MaxMyResults = 20;

    private IEnumerable<CompetitionOptions> Competitions
        => options.CurrentValue.Competitions.Where(c => c.Enabled && c.Name.Length > 0);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    [Command("CMD_GET_COMPLG_KIND_LIST", Roles = Roles)]
    public ValueTask<KvMessage[]> GetKindList(CommandContext ctx)
    {
        var kinds = Competitions.GroupBy(c => c.Kind)
            .Select(g => new KvMessage().Set("compe_kind", g.Key).Set("compe_name", g.First().Name))
            .ToArray();
        return Reply.Of(ctx.Ok().SetList("compe_kind_num", "compe_kind_list", kinds));
    }

    /// <summary>
    /// compe_list_kind (enum 0x0b) must be echoed, and for OLD_ALL compe_kind too; otherwise the
    /// client hardcodes KONAMI_CUP_ONLINE, so the kind only filters the archive.
    /// </summary>
    [Command("CMD_GET_COMPLG_LIST", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetList(CommandContext ctx)
    {
        var listKind = ctx.Request.GetString("compe_list_kind") ?? "NOW_PLAY";
        var kind = ctx.Request.GetString("compe_kind");
        var now = Now;
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);

        bool Listed(CompetitionOptions c) => listKind switch
        {
            "SCHEDULE" => now < c.EntryStart,
            "NOW_PLAY" or "NOW_WATCH" => now >= c.EntryStart && now < c.End,
            "OLD_ALL" => now >= c.End && c.Kind == kind,
            "OLD_MY" => now >= c.End && player?.CompetitionId == c.Id,
            _ => false,
        };

        var entries = new List<KvMessage>();
        foreach (var c in Competitions.Where(Listed).OrderBy(c => c.EntryStart).Take(MaxListed))
            entries.Add(await Summary(c, ctx.CancellationToken));

        var reply = ctx.Ok().Set("compe_list_kind", listKind);
        if (listKind == "OLD_ALL")
            reply.Set("compe_kind", kind ?? "");
        return [reply.SetList("compe_num", "compe_list", entries)];
    }

    /// <summary>FUN_0055daf0: everything sits in compe_data, whose compe_id must be the one asked for.</summary>
    [Command("CMD_GET_COMPLG_INFO", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetInfo(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var teams = TitleProfiles.UseableTeams(options.CurrentValue.GameProfile);
        var data = (await Summary(c, ctx.CancellationToken))
            .Set("use_national", "TRUE")
            .Set("use_club", "TRUE")
            .Set("use_ml", "FALSE")
            .Set("useable_team_size", teams.Length)
            .Set("useable_team_id", new KvArray(teams.Cast<object?>().ToArray()))
            .Set("usable_ball_pre_num", 0)
            .Set("usable_ball_playoff_num", 0)
            .Set("usable_ball_final_num", 0)
            // Preliminary, playoff and final rounds.
            .Set("gametime", KvArray.Repeat(c.GameTime, 3))
            .Set("fee", 0)
            .Set("is_digest", "NO");
        return [ctx.Ok().Set("compe_data", data)];
    }

    /// <summary>FUN_0055c9d0: the entered compe_id, 0 when not entered or the entry is over.</summary>
    [Command("CMD_GET_COMPLG_MYENTRY_COMPEID", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyEntry(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var entered = player is not null && Find(player.CompetitionId) is { } c && Now < c.End ? c.Id : 0;
        return [ctx.Ok().Set("compe_id", entered)];
    }

    /// <summary>
    /// Only the error names in the client's table for CMD_JOIN_COMPLG are used. The NOERR reply
    /// has to carry <c>is_select_team</c> (YES/NO; FUN_0055f140): without it the client drops the
    /// reply as a failed join and stays on the detail page. It says whether the player has picked
    /// a team yet (CMD_SET_COMPLG_TEAM): NO for a new entry.
    /// </summary>
    [Command("CMD_JOIN_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Join(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_DATABASE")];
        if (player.CompetitionId == c.Id)
        {
            await players.RecordCompetitionEntryAsync(player.Pid, c.Id, c.Kind, c.Name, ctx.CancellationToken);
            return [ctx.Ok().Set("is_select_team", player.CompetitionTeamId > 0 ? "YES" : "NO")];
        }

        var now = Now;
        if (now < c.EntryStart)
            return [ctx.Fail("ERR_COMPE_BEFORE_ENTRYTIME")];
        if (now >= c.EntryEnd)
            return [ctx.Fail("ERR_COMPE_AFTER_ENTRYTIME")];
        if (Find(player.CompetitionId) is { } other && now < other.End)
            return [ctx.Fail("ERR_COMPE_JOIN_OTHERCOMPE")];
        // ponytail: count-then-save race can overfill by a player or two; a DB constraint if it matters.
        if (await players.CountInCompetitionAsync(c.Id, ctx.CancellationToken) >= c.Capacity)
            return [ctx.Fail("ERR_COMPE_FULL")];

        player.CompetitionId = c.Id;
        (player.CompetitionTeamId, player.CompetitionTeamName, player.CompetitionTeam) = (0, "", "");
        await players.SaveAsync(ctx.CancellationToken);
        await players.RecordCompetitionEntryAsync(player.Pid, c.Id, c.Kind, c.Name, ctx.CancellationToken);
        return [ctx.Ok().Set("is_select_team", "NO")];
    }

    /// <summary>
    /// The Popularity Ranking button (FUN_0055ec30): teams by how many entrants picked them, as
    /// <c>rank_list</c> entries of team_id and user_num.
    /// </summary>
    [Command("CMD_GET_COMPLG_TEAMRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetTeamRanking(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var teams = (await players.GetCompetitionTeamCountsAsync(c.Id, ctx.CancellationToken))
            .Select(t => new KvMessage().Set("team_id", t.TeamId).Set("user_num", t.Players)).ToList();
        return [ctx.Ok().SetList("rank_list_num", "rank_list", teams)];
    }

    /// <summary>
    /// The exit icon of the competition screens sends this each time, and the team picked stays: it
    /// only ends the visit, so the next join finds the entry (and, with is_select_team YES, the
    /// team) as it was. Withdrawing is <see cref="Quit"/>.
    /// </summary>
    [Command("CMD_LEAVE_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Leave(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        return player is null || player.CompetitionId == 0 ? [ctx.Fail("ERR_COMPE_NOTENTRY")] : [ctx.Ok()];
    }

    /// <summary>Withdraws from the competition: the entry and the team go (the entry stays counted).</summary>
    [Command("CMD_QUIT_COMPLG", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> Quit(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || player.CompetitionId == 0)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        (player.CompetitionId, player.CompetitionTeamId, player.CompetitionTeamName, player.CompetitionTeam) = (0, 0, "", "");
        await players.SaveAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// FUN_00561a10 treats a reply without compe_name/trophy/name/team_id/pid/npid as "no
    /// champion yet", which is the truth until competition results exist.
    /// </summary>
    [Command("CMD_GET_COMPLG_TITLEHOLDER", Roles = Roles)]
    public ValueTask<KvMessage[]> GetTitleHolder(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>The competition the player has entered, which the qualifying round screens ask about without naming it.</summary>
    private async Task<(CompetitionOptions? Cup, KvMessage? Error)> EnteredAsync(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        return player is not null && Find(player.CompetitionId) is { } c ? (c, null) : (null, ctx.Fail("ERR_COMPE_NOTENTRY"));
    }

    /// <summary>"Number of Users / Max Capacity" on the qualifying round screen (FUN_00560500: just player_num).</summary>
    [Command("CMD_REQWATCH_COMPLG_PLAYERNUM", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> WatchPlayerNum(CommandContext ctx)
    {
        // ponytail: not pushed when others enter or leave; the count is read when the screen opens.
        var (cup, error) = await EnteredAsync(ctx);
        return cup is null
            ? [error!]
            : [ctx.Ok().Set("player_num", await players.CountInCompetitionAsync(cup.Id, ctx.CancellationToken))];
    }

    /// <summary>
    /// FUN_00561180: <c>is_waiting_time</c> TRUE with the <c>match_start_time</c> to wait for, FALSE
    /// once the matches may be played.
    /// </summary>
    [Command("CMD_GET_COMPLG_MATCH_START_TIME", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMatchStartTime(CommandContext ctx)
    {
        var (cup, error) = await EnteredAsync(ctx);
        if (cup is null)
            return [error!];

        var waiting = Now < cup.MatchStart;
        var reply = ctx.Ok().Set("is_waiting_time", waiting ? "TRUE" : "FALSE");
        return [waiting ? reply.Set("match_start_time", Unix(cup.MatchStart)) : reply];
    }

    /// <summary>One entrant's line in a competition: who, the team picked, and the totals of the matches played.</summary>
    private sealed record Line(Player Player, Tally Tally, long UpdatedAt)
    {
        public int Points => Tally.Wins * 3 + Tally.Draws;
        public int Difference => Tally.GoalsFor - Tally.GoalsAgainst;
        public int TeamId => Player.CompetitionTeamId;

        /// <summary>The client insists on a non-empty team_name, so a dash stands in for no team.</summary>
        public string TeamName => Player.CompetitionTeamName.Length > 0 ? Player.CompetitionTeamName : "-";
    }

    /// <summary>The competition's finished matches (a void one, nobody's fault, is left out by <see cref="CommunityStats.Of"/>).</summary>
    private async Task<List<MatchRecord>> MatchesAsync(int compeId, CancellationToken ct)
        => await db.Matches.AsNoTracking().Where(m => m.CompeId == compeId && m.Completed).OrderBy(m => m.PlayedAt).ToListAsync(ct);

    /// <summary>Everyone who entered or played the competition, with their totals, best first (points, goal difference, goals scored).</summary>
    private async Task<List<Line>> TableAsync(int compeId, List<MatchRecord> matches, CancellationToken ct)
    {
        var pids = matches.SelectMany(m => new[] { m.HomePid, m.AwayPid }).ToHashSet();
        pids.UnionWith(await db.Players.AsNoTracking().Where(p => p.CompetitionId == compeId).Select(p => p.Pid).ToListAsync(ct));
        var found = await db.Players.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToListAsync(ct);

        return found.Select(p => new Line(p,
                CommunityStats.Of(matches, m => m.HomePid == p.Pid ? true : m.AwayPid == p.Pid ? false : null),
                matches.Where(m => m.HomePid == p.Pid || m.AwayPid == p.Pid).Select(m => new DateTimeOffset(DateTime.SpecifyKind(m.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds()).DefaultIfEmpty(0).Max()))
            .OrderByDescending(l => l.Points).ThenByDescending(l => l.Difference).ThenByDescending(l => l.Tally.GoalsFor).ThenBy(l => l.Player.Pid)
            .ToList();
    }

    /// <summary>1 plus the entrants strictly ahead on <paramref name="key"/>: ties share a rank.</summary>
    private static int RankOf<T>(IEnumerable<Line> lines, Line line, Func<Line, T> key) where T : IComparable<T>
        => 1 + lines.Count(o => key(o).CompareTo(key(line)) > 0);

    /// <summary>
    /// Standings (FUN_00562e60): every entrant, three points for a win and one for a draw. An entry is
    /// rank, rank_by_team, team_id, pid, win/lose/draw_count, point, score (goals for), loss (goals
    /// against), scorediff, update_date, rating, ml_point, continuous_win, npid, is_qualifier
    /// (YES/NO), name and team_name.
    /// </summary>
    [Command("CMD_GET_COMPLG_PRE_RESULTS", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetPreResults(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var table = await TableAsync(c.Id, await MatchesAsync(c.Id, ctx.CancellationToken), ctx.CancellationToken);
        var rows = table.Select(l =>
        {
            var rank = 1 + table.Count(o => (o.Points, o.Difference, o.Tally.GoalsFor).CompareTo((l.Points, l.Difference, l.Tally.GoalsFor)) > 0);
            return new KvMessage()
                .Set("rank", rank).Set("rank_by_team", rank)
                .Set("team_id", l.TeamId).Set("pid", l.Player.Pid)
                .Set("win_count", l.Tally.Wins).Set("lose_count", l.Tally.Losses).Set("draw_count", l.Tally.Draws)
                .Set("point", l.Points).Set("score", l.Tally.GoalsFor).Set("loss", l.Tally.GoalsAgainst).Set("scorediff", l.Difference)
                .Set("update_date", l.UpdatedAt).Set("rating", l.Player.Rating).Set("ml_point", 0).Set("continuous_win", l.Tally.WinRun)
                .Set("npid", "").Set("is_qualifier", "NO").Set("name", l.Player.Name).Set("team_name", l.TeamName);
        }).ToList();

        return [Page(ctx, rows).Set("compe_name", c.Name).Set("date", Unix(Now))];
    }

    /// <summary>
    /// Goal and assist rankings (FUN_005620a0, FUN_00562760): the entrants by goals scored or assists
    /// made. An entry is rank, team_id, pid, score|assist, npid, name, team_name.
    /// </summary>
    [Command("CMD_GET_COMPLG_GOALRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    [Command("CMD_GET_COMPLG_ASSISTRANKING", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetScorerRanking(CommandContext ctx)
    {
        if (Find(ctx.Request.GetInt32("compe_id", 0)) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTFOUND")];

        var assists = ctx.Msg == "CMD_GET_COMPLG_ASSISTRANKING";
        int Value(Line l) => assists ? l.Tally.Assists : l.Tally.GoalsFor;
        var table = (await TableAsync(c.Id, await MatchesAsync(c.Id, ctx.CancellationToken), ctx.CancellationToken))
            .Where(l => l.Tally.Matches > 0).OrderByDescending(Value).ThenBy(l => l.Player.Pid).ToList();

        var rows = table.Select(l => new KvMessage()
            .Set("rank", RankOf(table, l, Value)).Set("team_id", l.TeamId).Set("pid", l.Player.Pid)
            .Set(assists ? "assist" : "score", Value(l))
            .Set("npid", "").Set("name", l.Player.Name).Set("team_name", l.TeamName)).ToList();
        return [Page(ctx, rows)];
    }

    /// <summary>
    /// A ranking reply: the part of <paramref name="rows"/> from start_rank on (get_count of them, the
    /// client takes at most 1000), with where it starts and how many there are in all.
    /// </summary>
    private static KvMessage Page(CommandContext ctx, IReadOnlyList<KvMessage> rows)
    {
        var start = Math.Max(1, ctx.Request.GetInt32("start_rank", 1));
        var shown = rows.Skip(start - 1).Take(Math.Clamp(ctx.Request.GetInt32("get_count", 100), 0, 1000)).ToList();
        return ctx.Ok().Set("start_rank", start).Set("rank_count", rows.Count).SetList("rank_num", "rank_list", shown);
    }

    /// <summary>
    /// "My results" (FUN_005687a0): the player's own place in the standings and their latest
    /// matches (the client takes 20), each with both sides' pid, manner, rating, team, name,
    /// division and the goals per period.
    /// </summary>
    [Command("CMD_GET_COMPLG_MYRESULTS", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetMyResults(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(player.CompetitionId) is not { } c)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        var matches = await MatchesAsync(c.Id, ctx.CancellationToken);
        var table = await TableAsync(c.Id, matches, ctx.CancellationToken);
        var me = table.FirstOrDefault(l => l.Player.Pid == player.Pid);
        var rank = me is null ? 0 : 1 + table.Count(o => (o.Points, o.Difference, o.Tally.GoalsFor).CompareTo((me.Points, me.Difference, me.Tally.GoalsFor)) > 0);

        var mine = matches.Where(m => (m.HomePid == player.Pid || m.AwayPid == player.Pid) && !(m.Disconnected && m.DisconnectedPid is null))
            .OrderByDescending(m => m.PlayedAt).Take(MaxMyResults).ToList();
        var byPid = table.ToDictionary(l => l.Player.Pid);
        KvMessage Side(int pid, int teamId, string periods, int goals)
        {
            var p = byPid.GetValueOrDefault(pid)?.Player;
            var team = p is not null && (teamId == 0 || teamId == p.CompetitionTeamId) ? p : null;
            var scores = periods.Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
            if (scores.Length != 5)
                scores = [goals, 0, 0, 0, 0];

            return new KvMessage()
                .Set("pid", pid).Set("manner", p?.Manner ?? 3).Set("rating", p?.Rating ?? 0)
                .Set("team_id", teamId != 0 ? teamId : p?.CompetitionTeamId ?? 0)
                .Set("name", p?.Name ?? "").Set("division", p is null ? "ML_DIVISION_5" : RankSystem.Label(p.Division, ctx.Profile))
                .Set("is_guest_player", "FALSE").Set("npid", "")
                .Set("team_name", team is { CompetitionTeamName.Length: > 0 } ? team.CompetitionTeamName : "-")
                .Set("score", new KvMessage().Set("score_1st", scores[0]).Set("score_2nd", scores[1])
                    .Set("score_ex1st", scores[2]).Set("score_ex2nd", scores[3]).Set("score_pk", scores[4]));
        }

        var results = mine.Select(m => new KvMessage()
            .Set("date", new DateTimeOffset(DateTime.SpecifyKind(m.PlayedAt, DateTimeKind.Utc)).ToUnixTimeSeconds())
            .Set("home", Side(m.HomePid, m.HomeTeamId, m.HomePeriods, m.HomeScore))
            .Set("away", Side(m.AwayPid, m.AwayTeamId, m.AwayPeriods, m.AwayScore))).ToList();

        return [ctx.Ok().Set("team_id", player.CompetitionTeamId).Set("team_name", player.CompetitionTeamName.Length > 0 ? player.CompetitionTeamName : "-")
            .Set("my_rank", rank).SetList("match_result_num", "match_result_list", results)];
    }

    /// <summary>
    /// The team the player picks for the competition: team_id, team_name, member_num and the
    /// footballers' details, kept as sent and played back by <see cref="GetTeamData"/>. The client's
    /// member list ends with a comma, which <see cref="KvReader"/> accepts.
    /// </summary>
    [Command("CMD_SET_COMPLG_TEAM", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> SetTeam(CommandContext ctx)
    {
        var player = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        if (player is null || Find(player.CompetitionId) is null)
            return [ctx.Fail("ERR_COMPE_NOTENTRY")];

        player.CompetitionTeamId = ctx.Request.GetInt32("team_id", 0);
        player.CompetitionTeamName = ctx.Request.GetString("team_name") ?? "";
        player.CompetitionTeam = new KvWriter().Write(new KvMessage().Set("member", ctx.Request.GetValue("member") ?? new List<KvMessage>())).TrimEnd('\0');
        await players.SaveAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// The squad an entrant picked (request: pid; FUN_005640f0 reads member_num, team_id, team_name and
    /// the member list). Someone who has picked none gets the client's own error for it.
    /// </summary>
    [Command("CMD_GET_COMPLG_TEAM_DATA", Roles = Roles, RequiredState = SessionState.PlayerSelected)]
    public async ValueTask<KvMessage[]> GetTeamData(CommandContext ctx)
    {
        var me = await players.GetAsync(ctx.Session.Pid, ctx.CancellationToken);
        var other = await players.GetAsync(ctx.Request.GetInt32("pid", ctx.Session.Pid), ctx.CancellationToken);
        if (me is null || other is null || me.CompetitionId == 0 || other.CompetitionId != me.CompetitionId)
            return [ctx.Fail("ERR_PLAYERNOTINCOMPE")];
        if (other.CompetitionTeamId == 0 || other.CompetitionTeamName.Length == 0)
            return [ctx.Fail("ERR_COMPE_NOT_SELECT_TEAM")];

        object members = new List<KvMessage>();
        if (other.CompetitionTeam.Length > 0 && new KvReader().TryParse(other.CompetitionTeam)?.GetValue("member") is { } stored)
            members = stored;

        // The player results screen shows each footballer's goals, assists and cards in this competition.
        if (members is IReadOnlyList<KvMessage> squad && other.CompetitionId != 0)
        {
            var played = await db.Matches.AsNoTracking()
                .Where(m => m.CompeId == other.CompetitionId && m.Completed && (m.HomePid == other.Pid || m.AwayPid == other.Pid))
                .Select(m => new { m.HomePid, m.Events }).ToListAsync(ctx.CancellationToken);
            members = CommunityStats.WithTotals(squad, played.Select(m => (m.HomePid == other.Pid ? "HOME" : "AWAY", m.Events)));
        }

        var count = members is IReadOnlyList<KvMessage> list ? list.Count : 0;
        // FUN_005640f0 drops the reply, and the match with it, unless pid and name (the owner) come along.
        return [ctx.Ok().Set("pid", other.Pid).Set("name", other.Name).Set("team_id", other.CompetitionTeamId).Set("team_name", other.CompetitionTeamName).Set("member_num", count).Set("member", members)];
    }

    private CompetitionOptions? Find(int id) => id == 0 ? null : Competitions.FirstOrDefault(c => c.Id == id);

    /// <summary>The keys the list and the detail page share.</summary>
    private async Task<KvMessage> Summary(CompetitionOptions c, CancellationToken ct)
        => new KvMessage()
            .Set("compe_kind", c.Kind)
            .Set("compe_id", c.Id)
            .Set("entry_start_time", Unix(c.EntryStart))
            .Set("entry_end_time", Unix(c.EntryEnd))
            .Set("match_start_time", Unix(c.MatchStart))
            .Set("match_end_time", Unix(c.MatchEnd))
            .Set("compe_end_time", Unix(c.End))
            .Set("compe_name", c.Name)
            .Set("status", Status(c, Now))
            .Set("category", c.Category)
            .Set("entry_limit", "DIVISION")
            .Set("entry_limit_rank", 0)
            // Non-empty, at most 15 characters (FUN_0055cdb0).
            .Set("dlcver", c.DlcVersion.Length > 0 ? c.DlcVersion : options.CurrentValue.Protocol.DlcVersion)
            .Set("player_capacity", c.Capacity)
            .Set("player_num", await players.CountInCompetitionAsync(c.Id, ct))
            .Set("champion_notice", "FALSE")
            .Set("guest_count", 0)
            .Set("is_coach_mode", "NO")
            .Set("need_konamiid", "NO")
            // D5..D1 and NONE, enum 0x01: every division may enter.
            .Set("entry_limit_division", KvArray.Repeat("TRUE", 6));

    /// <summary>Enum 0x0e, derived from the configured times.</summary>
    internal static string Status(CompetitionOptions c, DateTime now)
        => now < c.EntryStart ? "CREATED"
            : now < c.EntryEnd && now < c.MatchStart ? "ENTRY"
            : now < c.MatchEnd ? (now < c.MatchStart ? "ENTRY_CLOSED" : "STARTED")
            : now < c.End ? "MATCH_CLOSED"
            : "FINISHED";

    private static long Unix(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

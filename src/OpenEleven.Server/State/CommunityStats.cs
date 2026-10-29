// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Globalization;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>One side's totals over a set of community matches.</summary>
public sealed class Tally
{
    public int Matches, Wins, Losses, Draws;
    public int GoalsFor, GoalsAgainst, Assists, Fouls, Yellow, Red, Shots, ShotsAgainst;
    public int PossessionSum, PossessionCount;
    public int WinRun, WinRunMax, LoseRun, LoseRunMax;

    /// <summary>A total spread over the matches played, rounded to a whole number.</summary>
    public int Average(int total) => Matches == 0 ? 0 : (int)Math.Round((double)total / Matches, MidpointRounding.AwayFromZero);

    /// <summary>Share of the matches won, in percent.</summary>
    public int WinPercent => Matches == 0 ? 0 : (int)Math.Round(100.0 * Wins / Matches, MidpointRounding.AwayFromZero);

    public int PossessionAverage => PossessionCount == 0 ? 0 : (int)Math.Round((double)PossessionSum / PossessionCount, MidpointRounding.AwayFromZero);
}

/// <summary>One cell's line in a league table: three points for a win, one for a draw.</summary>
public sealed record Standing(int CellId, int Won, int Drawn, int Lost, int For, int Against)
{
    public int Points => Won * 3 + Drawn;

    public int Difference => For - Against;
}

/// <summary>
/// The figures the community record and ranking screens show, worked out from the community's
/// finished matches. The match host's reports supply the rest: goals, assists and cards come
/// from <c>GOAL</c> and <c>CARD</c> events, fouls from <c>FOUL</c> and possession and shots from
/// <c>MATCHINFO|possession_h|shot_h|suffer_shot_h|possession_a|shot_a|suffer_shot_a</c>.
/// </summary>
public static class CommunityStats
{
    /// <summary>
    /// Totals for whoever <paramref name="isHome"/> picks out: true when they were the home side of
    /// the match, false when away, null when they did not play it.
    /// </summary>
    public static Tally Of(IEnumerable<MatchRecord> matches, Func<MatchRecord, bool?> isHome)
    {
        var t = new Tally();
        foreach (var m in matches.OrderBy(m => m.PlayedAt))
        {
            if (isHome(m) is not { } home || (m.Disconnected && m.DisconnectedPid is null))
                continue;

            var us = home ? "HOME" : "AWAY";
            var outcome = Outcome(m, home);
            t.Matches++;
            t.GoalsFor += home ? m.HomeScore : m.AwayScore;
            t.GoalsAgainst += home ? m.AwayScore : m.HomeScore;

            if (outcome > 0)
            {
                t.Wins++;
                t.WinRun++;
                t.LoseRun = 0;
            }
            else if (outcome < 0)
            {
                t.Losses++;
                t.LoseRun++;
                t.WinRun = 0;
            }
            else
            {
                t.Draws++;
                t.WinRun = t.LoseRun = 0;
            }

            t.WinRunMax = Math.Max(t.WinRunMax, t.WinRun);
            t.LoseRunMax = Math.Max(t.LoseRunMax, t.LoseRun);

            foreach (var line in m.Events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('|');
                switch (p[0])
                {
                    // GOAL|phase|team|pid|gameplayer|side|assist_side|assist_gameplayer|assist_player_side
                    case "GOAL" when p.Length > 7 && p[2] == us && p[7] != "0":
                        t.Assists++;
                        break;
                    // CARD|phase|side|kind|pid|gameplayer
                    case "CARD" when p.Length > 3 && p[2] == us:
                        if (p[3] == "YELLOW") t.Yellow++;
                        else if (p[3] == "RED") t.Red++;
                        break;
                    // FOUL|phase|side
                    case "FOUL" when p.Length > 2 && p[2] == us:
                        t.Fouls++;
                        break;
                    case "MATCHINFO" when p.Length > 6:
                        var at = home ? 1 : 4;
                        t.PossessionSum += Number(p[at]);
                        t.PossessionCount++;
                        t.Shots += Number(p[at + 1]);
                        t.ShotsAgainst += Number(p[at + 2]);
                        break;
                }
            }
        }

        return t;
    }

    /// <summary>
    /// A squad with each footballer's <c>goal</c>, <c>assist</c>, <c>total_yellow_card</c> and
    /// <c>total_red_card</c> counted from the events of the matches it played (the side it played
    /// and the match's event lines). The client saves its squads with the counters at zero, so they
    /// are laid over it here, matched on <c>gameplayer_id</c>. Goals are counted by side, not by the
    /// scorer's pid, which is 0 when the scorer is not under a player's control (CPU); an own goal
    /// (scorer's side differs from the credited side) is nobody's goal.
    /// </summary>
    public static IReadOnlyList<KvMessage> WithTotals(IReadOnlyList<KvMessage> squad, IEnumerable<(string Side, string Events)> played)
    {
        var goals = new Dictionary<int, int>();
        var assists = new Dictionary<int, int>();
        var yellows = new Dictionary<int, int>();
        var reds = new Dictionary<int, int>();
        static void Add(Dictionary<int, int> to, string player)
        {
            if (int.TryParse(player, out var id) && id != 0)
                to[id] = to.GetValueOrDefault(id) + 1;
        }

        foreach (var (side, events) in played)
        {
            foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = line.Split('|');
                switch (p[0])
                {
                    // GOAL|phase|team|pid|gameplayer|side|assist_side|assist_gameplayer|assist_player_side
                    case "GOAL" when p.Length > 7 && p[2] == side && p[5] == side:
                        Add(goals, p[4]);
                        Add(assists, p[7]);
                        break;
                    // CARD|phase|side|kind|pid|gameplayer
                    case "CARD" when p.Length > 5 && p[2] == side:
                        Add(p[3] == "RED" ? reds : yellows, p[5]);
                        break;
                }
            }
        }

        // A message cannot replace a field, so each member is copied without the four counters.
        string[] counters = ["goal", "assist", "total_yellow_card", "total_red_card"];
        return squad.Select(member =>
        {
            var id = member.GetInt32("gameplayer_id", 0);
            var copy = new KvMessage();
            foreach (var (key, value) in member.Fields)
                if (!counters.Contains(key))
                    copy.Set(key, value);

            return copy.Set("goal", goals.GetValueOrDefault(id)).Set("assist", assists.GetValueOrDefault(id))
                .Set("total_yellow_card", yellows.GetValueOrDefault(id)).Set("total_red_card", reds.GetValueOrDefault(id));
        }).ToList();
    }

    /// <summary>Every cell's line, best first (points, goal difference, goals scored, then cell id).</summary>
    public static List<Standing> Table(IEnumerable<int> cellIds, IEnumerable<CommunityLeagueMatch> finished)
    {
        var lines = cellIds.ToDictionary(id => id, id => new int[5]); // won, drawn, lost, for, against
        foreach (var m in finished.Where(m => m.Finished))
        {
            if (!lines.TryGetValue(m.HomeCellId, out var home) || !lines.TryGetValue(m.AwayCellId, out var away))
                continue;

            home[3] += m.HomeGoals; home[4] += m.AwayGoals;
            away[3] += m.AwayGoals; away[4] += m.HomeGoals;
            var diff = Math.Sign(m.HomeGoals - m.AwayGoals);
            home[diff > 0 ? 0 : diff < 0 ? 2 : 1]++;
            away[diff < 0 ? 0 : diff > 0 ? 2 : 1]++;
        }

        return lines.Select(l => new Standing(l.Key, l.Value[0], l.Value[1], l.Value[2], l.Value[3], l.Value[4]))
            .OrderByDescending(s => s.Points).ThenByDescending(s => s.Difference).ThenByDescending(s => s.For).ThenBy(s => s.CellId)
            .ToList();
    }

    /// <summary>1 won, -1 lost, 0 drawn: the score, then the shoot-out, and a disconnect loses.</summary>
    private static int Outcome(MatchRecord m, bool home)
    {
        if (m.DisconnectedPid is { } leaver)
        {
            var homeLeft = leaver == m.HomePid;
            return homeLeft == home ? -1 : 1;
        }

        var diff = m.HomeScore - m.AwayScore;
        if (diff == 0)
            diff = Shootout(m.HomePeriods) - Shootout(m.AwayPeriods);

        return Math.Sign(home ? diff : -diff);
    }

    /// <summary>Goals in the shoot-out, the last of "1st,2nd,ex1st,ex2nd,pk".</summary>
    private static int Shootout(string periods)
    {
        var parts = periods.Split(',');
        return parts.Length == 5 && int.TryParse(parts[4], out var pk) ? pk : 0;
    }

    private static int Number(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}

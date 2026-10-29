// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data;
using OpenEleven.Protocol.Kv;

namespace OpenEleven.Server.State;

/// <summary>
/// The head-to-head screens of a community: two members, or two teams, against each other. The
/// client does not ask the socket; CMD_GET_COMMU_MEMBERSRESULTS_VS and CMD_GET_COMMU_TEAMRESULTS_VS
/// become an HTTP request to the COMMU_MEMBERS_VS / COMMU_TEAM_VS url with
/// <c>commu_id=..&amp;base_pid=..&amp;oppo_pid=..</c> (or base_team_id / oppo_team_id) after it
/// (FUN_011be170), and the body that comes back is taken as the reply itself
/// (FUN_011be0f0): <c>result="NOERR",stats=[{...}]</c>, the entry FUN_00552990 reads.
/// </summary>
public static class CommunityVersus
{
    public static Task<string> MembersAsync(GameDbContext db, string query, CancellationToken ct)
        => RenderAsync(db, query, "base_pid", "oppo_pid", members: true, ct);

    public static Task<string> TeamsAsync(GameDbContext db, string query, CancellationToken ct)
        => RenderAsync(db, query, "base_team_id", "oppo_team_id", members: false, ct);

    private static async Task<string> RenderAsync(GameDbContext db, string query, string baseKey, string oppoKey, bool members, CancellationToken ct)
    {
        // The url may already end in "?", and the client may add its own, so leading ones are dropped.
        var q = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.Last()[1]);
        int Arg(string key) => q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 0;

        var commuId = Arg("commu_id");
        var (a, b) = (Arg(baseKey), Arg(oppoKey));
        if (!await db.Communities.AnyAsync(c => c.Id == commuId, ct))
            return Error("ERR_COMMUNOTEXIST");

        if (members && await db.CommunityMembers.CountAsync(m => m.CommuId == commuId && (m.Pid == a || m.Pid == b), ct) < (a == b ? 1 : 2))
            return Error("ERR_PLAYERNOTINCOMMU");

        var matches = await db.Matches.AsNoTracking().Where(m => m.CommuId == commuId && m.Completed).ToListAsync(ct);
        var tally = members
            ? CommunityStats.Of(matches.Where(m => (m.HomePid == a && m.AwayPid == b) || (m.HomePid == b && m.AwayPid == a)),
                m => m.HomePid == a ? true : m.AwayPid == a ? false : null)
            : CommunityStats.Of(matches.Where(m => (m.HomeTeamId == a && m.AwayTeamId == b) || (m.HomeTeamId == b && m.AwayTeamId == a)),
                m => m.HomeTeamId == a ? true : m.AwayTeamId == a ? false : null);

        var stats = new KvMessage()
            .Set("match_count", tally.Matches).Set("win_count", tally.Wins).Set("lose_count", tally.Losses).Set("draw_count", tally.Draws)
            .Set("win_average", tally.WinPercent).Set("possession_average", tally.PossessionAverage);
        foreach (var (key, total) in new[] { ("score_get", tally.GoalsFor), ("score_lost", tally.GoalsAgainst), ("shot", tally.Shots), ("suffer_shot", tally.ShotsAgainst) })
            stats.Set(key, new List<KvMessage> { new KvMessage().Set("total_count", total).Set("average", tally.Average(total)) });

        return new KvWriter().Write(new KvMessage().Set("result", "NOERR").Set("stats", new List<KvMessage> { stats })).TrimEnd('\0');
    }

    private static string Error(string result) => $"result=\"{result}\",stats=\"\"";
}

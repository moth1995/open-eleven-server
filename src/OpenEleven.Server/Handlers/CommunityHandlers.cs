// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenEleven.Data;
using OpenEleven.Data.Entities;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Communities, from PES 2011 on. PES 2011 serves real ones: create, list, search, rank, join,
/// manage and leave. Reply shapes come from pes2011.exe: FUN_005255a0 (joined list, at most 5
/// entries), FUN_005260f0 (search), FUN_0054e980 (one community entry) and FUN_00549460 (its owner
/// block). Matches, leagues and team results are not served yet. PES 2012 and later share these
/// handlers (their entries have not been read separately yet); the stubs at the end are for
/// commands only they send.
/// </summary>
public sealed class CommunityHandlers(GameDbContext db, ILogger<CommunityHandlers> log)
{
    /// <summary>FUN_005255a0 rejects a joined list of 6 or more.</summary>
    private const int MaxJoined = 5;

    /// <summary>CMD_REQWATCHL_COMMU_MEMBERLIST holds 32 entries.</summary>
    private const int MaxMembers = 32;

    private const int PageSize = 10;

    /// <summary>
    /// The client decides whether to ask for a password from the one it is sent, so a protected
    /// community shows this to everyone but its owner instead of the real password.
    /// </summary>
    private const string PasswordMask = "*";

    [Command("CMD_CREATE_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> CreateCommu(CommandContext ctx)
    {
        var name = (ctx.Request.GetString("commu_name") ?? "").Trim();
        if (name.Length == 0)
            return [ctx.Fail("ERR_DATABASE")];

        var pid = ctx.Session.Pid;
        if (await db.CommunityMembers.CountAsync(m => m.Pid == pid, ctx.CancellationToken) >= MaxJoined)
            return [ctx.Fail("ERR_TOOMANYCOMMU")];

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var community = new Community
        {
            OwnerPid = pid,
            Name = Cut(name, 77),
            Description = Cut(ctx.Request.GetString("commu_description") ?? "", 246),
            Password = Cut(ctx.Request.GetString("password") ?? "", 30),
            InviteOnly = ctx.Request.GetString("invite_only") == "YES",
            Week = WeekFlags(ctx.Request.GetValue("week")),
            StartTime = ctx.Request.GetInt32("start_time", 0),
            EndTime = ctx.Request.GetInt32("end_time", 0),
            CreatedAt = now,
        };
        db.Communities.Add(community);
        await db.SaveChangesAsync(ctx.CancellationToken);

        db.CommunityMembers.Add(new CommunityMember { CommuId = community.Id, Pid = pid, JoinedAt = now });
        await db.SaveChangesAsync(ctx.CancellationToken);

        log.LogInformation("Player {Pid} created community {Id} \"{Name}\"", pid, community.Id, community.Name);
        return [ctx.Ok().Set("commu_id", community.Id)];
    }

    [Command("CMD_GET_JOINEDCOMMULIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetJoinedCommuList(CommandContext ctx)
    {
        var pid = ctx.Session.Pid;
        var joined = await (from m in db.CommunityMembers.Where(m => m.Pid == pid)
                            join c in db.Communities on m.CommuId equals c.Id
                            orderby m.JoinedAt
                            select c).Take(MaxJoined).AsNoTracking().ToListAsync(ctx.CancellationToken);

        var entries = await EntriesAsync(ctx, joined);
        return [ctx.Ok().SetList("commu_num", "clist", entries)];
    }

    /// <summary>
    /// Finds communities by name or comment. Filters other than the text are applied in memory;
    /// ponytail: fine for a few thousand communities, push them into SQL if that stops holding.
    /// </summary>
    [Command("CMD_SEARCH_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> SearchCommu(CommandContext ctx)
    {
        var request = ctx.Request;
        var pid = ctx.Session.Pid;
        var keyword = (request.GetString("keyword") ?? "").Trim();
        var byComment = request.GetString("target") == "COMMENT";
        var lang = request.GetString("lang") ?? "ANY";
        var targetPid = request.GetInt32("target_pid", 0);
        var friendsOnly = request.GetString("has_friends") == "YES";
        var week = WeekFlags(request.GetValue("week"));
        var hour = HourIndex(request.GetString("active_time"));
        var index = Math.Max(0, request.GetInt32("index", 0));

        var query = Visible(pid);
        if (keyword.Length > 0)
            query = byComment ? query.Where(c => c.Description.Contains(keyword)) : query.Where(c => c.Name.Contains(keyword));
        if (lang != "ANY")
            query = query.Where(c => db.Players.Any(p => p.Pid == c.OwnerPid && p.Lang == lang));
        if (targetPid != 0)
            query = query.Where(c => db.CommunityMembers.Any(m => m.CommuId == c.Id && m.Pid == targetPid));
        if (friendsOnly)
            query = query.Where(c => db.CommunityMembers.Any(m => m.CommuId == c.Id
                && db.Friendships.Any(f => f.Pid == pid && f.OtherPid == m.Pid && f.State == FriendState.Friend)));

        var found = (await query.OrderByDescending(c => c.Id).ToListAsync(ctx.CancellationToken))
            .Where(c => PlaysOn(c, week) && ActiveAt(c, hour))
            .ToList();

        var entries = await EntriesAsync(ctx, found.Skip(index).Take(PageSize).ToList());
        return [ctx.Ok().SetList("commu_num", "mlist", entries).Set("total_found_num", found.Count)];
    }

    /// <summary>
    /// The ranking tabs. Each entry wraps the community in <c>commu_info</c> next to its
    /// <c>value</c> (FUN_0054fe50). Only the online-member ranking has a value yet; the match and
    /// league counts are not tracked, so those tabs list communities in one tie.
    /// </summary>
    [Command("CMD_SEARCH_COMMU_BYRANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> SearchCommuByRanking(CommandContext ctx)
    {
        var kind = ctx.Request.GetString("ranking_kind");
        var all = await Visible(ctx.Session.Pid).ToListAsync(ctx.CancellationToken);
        var entries = await EntriesAsync(ctx, all);

        var ranked = entries
            .Select(e => new KvMessage()
                .Set("value", kind == "LOGINMEMBER_NUM" ? e.GetInt32("logon_member_count") : 0)
                .Set("commu_info", e))
            .OrderByDescending(e => e.GetInt32("value"))
            .Take(PageSize)
            .ToList();
        return [ctx.Ok().SetList("commu_num", "mlist", ranked)];
    }

    [Command("CMD_JOIN_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> JoinCommu(CommandContext ctx)
    {
        var pid = ctx.Session.Pid;
        var id = ctx.Request.GetInt32("commu_id", 0);
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ctx.CancellationToken);
        if (community is null)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (await db.CommunityMembers.AnyAsync(m => m.CommuId == id && m.Pid == pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_ALREADYINCOMMU")];

        // Invitations are not served, so an invite-only community cannot be entered.
        if (community.InviteOnly)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (await db.CommunityMembers.CountAsync(m => m.Pid == pid, ctx.CancellationToken) >= MaxJoined)
            return [ctx.Fail("ERR_TOOMANYCOMMU")];

        if (await db.CommunityMembers.CountAsync(m => m.CommuId == id, ctx.CancellationToken) >= MaxMembers)
            return [ctx.Fail("ERR_COMMUNITYISFULL")];

        if (community.Password.Length > 0 && community.Password != (ctx.Request.GetString("password") ?? ""))
            return [ctx.Fail("ERR_PASSWDINCORRECT")];

        db.CommunityMembers.Add(new CommunityMember { CommuId = id, Pid = pid, JoinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        await db.SaveChangesAsync(ctx.CancellationToken);
        await PushMemberAsync(ctx, id, pid, "ADD");
        return [ctx.Ok()];
    }

    /// <summary>
    /// The client sends this when it opens a community. Commands that name no community (settings,
    /// delete, the team and league lists) act on this one.
    /// </summary>
    [Command("CMD_SET_CURRENT_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> SetCurrentCommu(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("commu_id", 0);
        if (!await db.Communities.AnyAsync(c => c.Id == id, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (!await db.CommunityMembers.AnyAsync(m => m.CommuId == id && m.Pid == ctx.Session.Pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_NOTJOINEDCOMMU")];

        if (ctx.Session.CurrentCommuId != id)
            ctx.Session.CurrentCompeId = null;
        ctx.Session.CurrentCommuId = id;
        ctx.Hub.EnterCommunityBlock(ctx.Session, id);
        await PushMemberAsync(ctx, id, ctx.Session.Pid);
        return [ctx.Ok()];
    }

    [Command("CMD_RESET_CURRENT_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> ResetCurrentCommu(CommandContext ctx)
    {
        var (commuId, compeId) = (ctx.Session.CurrentCommuId, ctx.Session.CurrentCompeId);
        Close(ctx.Hub, ctx.Session);
        if (commuId is { } id)
        {
            // The player is no longer in the community (or its league) on the others' screens.
            await PushMemberAsync(ctx, id, ctx.Session.Pid);
            if (compeId is { } compe)
                await PushLeagueMemberAsync(ctx, id, compe, ctx.Session.Pid);
        }

        return [ctx.Ok()];
    }

    /// <summary>One community for the settings screen (FUN_00529290): <c>commu_info</c> holds a single entry.</summary>
    [Command("CMD_GET_COMMU_INFO", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetCommuInfo(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("commu_id", 0);
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ctx.CancellationToken);
        List<KvMessage> entries = community is null ? [] : await EntriesAsync(ctx, [community]);
        return entries.Count == 0 ? [ctx.Fail("ERR_COMMUNOTEXIST")] : [ctx.Ok().Set("commu_info", entries)];
    }

    /// <summary>The owner's settings screen; it names no community, so it changes the current one.</summary>
    [Command("CMD_SET_COMMU_OPTION", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> SetCommuOption(CommandContext ctx)
    {
        var (community, error) = await OwnedCurrentAsync(ctx);
        if (community is null)
            return [error!];

        var request = ctx.Request;
        var name = (request.GetString("commu_name") ?? "").Trim();
        if (name.Length > 0)
            community.Name = Cut(name, 77);
        community.Description = Cut(request.GetString("commu_description") ?? community.Description, 246);
        community.Password = Cut(request.GetString("password") ?? community.Password, 30);
        community.InviteOnly = request.Has("invite_only") ? request.GetString("invite_only") == "YES" : community.InviteOnly;
        if (request.Has("week"))
            community.Week = WeekFlags(request.GetValue("week"));
        community.StartTime = request.GetInt32("start_time", community.StartTime);
        community.EndTime = request.GetInt32("end_time", community.EndTime);

        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>Removes the current community and everyone's membership of it.</summary>
    [Command("CMD_DEL_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> DeleteCommu(CommandContext ctx)
    {
        var (community, error) = await OwnedCurrentAsync(ctx);
        if (community is null)
            return [error!];

        db.CommunityMembers.RemoveRange(db.CommunityMembers.Where(m => m.CommuId == community.Id));
        db.Communities.Remove(community);
        await db.SaveChangesAsync(ctx.CancellationToken);

        foreach (var session in ctx.Hub.Sessions.Where(s => s.CurrentCommuId == community.Id))
            Close(ctx.Hub, session);

        log.LogInformation("Player {Pid} deleted community {Id}", ctx.Session.Pid, community.Id);
        return [ctx.Ok()];
    }

    [Command("CMD_DEL_COMMU_MEMBER", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> RemoveMember(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("commu_id", 0);
        var pid = ctx.Request.GetInt32("pid", 0);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == id, ctx.CancellationToken);
        if (community is null)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (community.OwnerPid != ctx.Session.Pid)
            return [ctx.Fail("ERR_NOTCOMMUOWNER")];

        var member = await db.CommunityMembers.FirstOrDefaultAsync(m => m.CommuId == id && m.Pid == pid, ctx.CancellationToken);
        if (member is null || pid == community.OwnerPid)
            return [ctx.Fail("ERR_PLAYERNOTINCOMMU")];

        db.CommunityMembers.Remove(member);
        await db.SaveChangesAsync(ctx.CancellationToken);
        foreach (var session in ctx.Hub.SessionsForPid(pid).Where(s => s.CurrentCommuId == id))
            Close(ctx.Hub, session);
        await PushMemberAsync(ctx, id, pid, "DEL");
        return [ctx.Ok()];
    }

    /// <summary>Leaving; the owner cannot (the client greys it out), only delete.</summary>
    [Command("CMD_WITHDRAW_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WithdrawCommu(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("commu_id", 0);
        var pid = ctx.Session.Pid;
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ctx.CancellationToken);
        if (community is null)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var member = await db.CommunityMembers.FirstOrDefaultAsync(m => m.CommuId == id && m.Pid == pid, ctx.CancellationToken);
        if (member is null)
            return [ctx.Fail("ERR_PLAYERNOTINCOMMU")];

        if (community.OwnerPid == pid)
            return [ctx.Fail("ERR_DATABASE")];

        db.CommunityMembers.Remove(member);
        await db.SaveChangesAsync(ctx.CancellationToken);
        if (ctx.Session.CurrentCommuId == id)
            Close(ctx.Hub, ctx.Session);
        await PushMemberAsync(ctx, id, pid, "DEL");
        return [ctx.Ok()];
    }

    /// <summary>Enum 0x10, in order.</summary>
    private static readonly string[] LeagueMatchTimes = ["TIME_5", "TIME_10", "TIME_15"];

    /// <summary>The client lists at most 3 open leagues (FUN_00526990 rejects compe_num of 4 or more).</summary>
    private const int MaxOpenLeagues = 3;

    /// <summary>Any member can open a league in the community they have open; it replies with the new compe_id.</summary>
    [Command("CMD_CREATE_COMMU_LEAGUE", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> CreateLeague(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId
            || !await db.Communities.AnyAsync(c => c.Id == commuId, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var pid = ctx.Session.Pid;
        if (!await db.CommunityMembers.AnyAsync(m => m.CommuId == commuId && m.Pid == pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_NOTJOINEDCOMMU")];

        if (await db.CommunityLeagues.CountAsync(l => l.CommuId == commuId && !l.Finished, ctx.CancellationToken) >= MaxOpenLeagues)
            return [ctx.Fail("ERR_COMMUCOMPEISFULL")];

        var league = new CommunityLeague
        {
            CommuId = commuId,
            ChairmanPid = pid,
            Name = Cut((ctx.Request.GetString("compe_name") ?? "").Trim(), 40),
            MatchTime = ctx.Request.GetString("match_time") ?? "TIME_5",
            Injury = ctx.Request.GetString("injury") == "YES",
            BallType = ctx.Request.GetInt32("ball_type", 0),
            HomeAndAway = ctx.Request.GetString("home_and_away") == "YES",
            Comment = Cut(ctx.Request.GetString("comment") ?? "", 255),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.CommunityLeagues.Add(league);
        await db.SaveChangesAsync(ctx.CancellationToken);

        ctx.Session.CurrentCompeId = league.Id;
        log.LogInformation("Player {Pid} opened league {Id} \"{Name}\" in community {Commu}", pid, league.Id, league.Name, commuId);
        return [ctx.Ok().Set("compe_id", league.Id)];
    }

    /// <summary>
    /// Signs the player up to a league of the community they have open. The reply is just
    /// <c>is_registered</c> (FUN_005278c0), YES once they are on the league's list.
    /// </summary>
    [Command("CMD_JOIN_COMMU_LEAGUE", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> JoinLeague(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var id = ctx.Request.GetInt32("compe_id", 0);
        if (!await db.CommunityLeagues.AnyAsync(l => l.Id == id && l.CommuId == commuId && !l.Finished, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        var pid = ctx.Session.Pid;
        if (!await db.CommunityMembers.AnyAsync(m => m.CommuId == commuId && m.Pid == pid, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (!await db.CommunityLeagueMembers.AnyAsync(m => m.CompeId == id && m.Pid == pid, ctx.CancellationToken))
        {
            db.CommunityLeagueMembers.Add(new CommunityLeagueMember { CompeId = id, Pid = pid });
            await db.SaveChangesAsync(ctx.CancellationToken);
        }

        ctx.Session.CurrentCompeId = id;
        await PushLeagueMemberAsync(ctx, commuId, id, pid);
        await PushMemberAsync(ctx, commuId, pid);
        return [ctx.Ok().Set("is_registered", "YES")];
    }

    /// <summary>The open leagues of the community the player has open (FUN_00556e40 reads each entry).</summary>
    [Command("CMD_GET_COMMU_LEAGUELIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueList(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var leagues = await db.CommunityLeagues.AsNoTracking()
            .Where(l => l.CommuId == commuId && !l.Finished).OrderBy(l => l.Id).Take(MaxOpenLeagues)
            .ToListAsync(ctx.CancellationToken);
        var pids = leagues.Select(l => l.ChairmanPid).Distinct().ToArray();
        var chairmen = await db.Players.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, ctx.CancellationToken);
        var online = OnlinePids(ctx);

        var entries = new List<KvMessage>();
        foreach (var l in leagues.Where(l => chairmen.ContainsKey(l.ChairmanPid)))
        {
            var cells = await CellEntriesAsync(ctx, l.Id);
            await EnsureFixturesAsync(l, ctx.CancellationToken);
            var fixtureCount = await db.CommunityLeagueMatches.CountAsync(m => m.CompeId == l.Id, ctx.CancellationToken);
            var playedCount = await db.CommunityLeagueMatches.CountAsync(m => m.CompeId == l.Id && m.Finished, ctx.CancellationToken);
            entries.Add(new KvMessage()
            .Set("compe_id", l.Id)
            .Set("opening_date", l.CreatedAt)
            .Set("entered_cell_count", cells.Count)
            .Set("finished_match_count", playedCount)
            .Set("total_match_count", fixtureCount)
            .Set("compe_chairman_info", new List<KvMessage> { PlayerBlock(ctx, chairmen[l.ChairmanPid], online.Contains(l.ChairmanPid)) })
            .Set("entry_info", cells)
            // FUN_00554c70 reads these four as 16-bit numbers, not the YES/NO and TIME_n names the
            // create request carries; a name here fails the whole entry and the screen is left.
            .Set("env_setting", new List<KvMessage>
            {
                new KvMessage()
                    .Set("compe_name", l.Name)
                    .Set("match_time", Math.Max(0, Array.IndexOf(LeagueMatchTimes, l.MatchTime)))
                    .Set("injury", l.Injury ? 1 : 0)
                    .Set("ball_type", l.BallType)
                    .Set("home_and_away", l.HomeAndAway ? 1 : 0)
                    .Set("comment", l.Comment),
            }));
        }

        return [ctx.Ok().SetList("compe_num", "compe_list", entries)];
    }

    /// <summary>The client lists at most 20 cells in a league (FUN_0054b190 and the count check in FUN_00556e40).</summary>
    private const int MaxCells = 20;

    /// <summary>
    /// How many members share a cell before ERR_ENTRYISFULL: one. The client reads each cell into a
    /// fixed slot with room for a single player block (FUN_0054b190), and always asks for a new cell.
    /// </summary>
    private const int MaxCellUsers = 1;

    /// <summary>The league named by the request, or else the one the player has open, inside the open community.</summary>
    private async Task<(CommunityLeague? League, KvMessage? Error)> LeagueAsync(CommandContext ctx, int? compeId = null, bool open = true)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return (null, ctx.Fail("ERR_COMMUNOTEXIST"));

        var id = compeId ?? ctx.Session.CurrentCompeId ?? 0;
        var league = await db.CommunityLeagues.FirstOrDefaultAsync(
            l => l.Id == id && l.CommuId == commuId && (!open || !l.Finished), ctx.CancellationToken);
        return league is null ? (null, ctx.Fail("ERR_COMMUCOMPENOTEXIST")) : (league, null);
    }

    /// <summary>The league's cells as FUN_0054b190 reads them: the cell, its team and the members in it.</summary>
    private async Task<List<KvMessage>> CellEntriesAsync(CommandContext ctx, int compeId)
    {
        var cells = await db.CommunityLeagueCells.AsNoTracking().Where(c => c.CompeId == compeId)
            .OrderBy(c => c.Id).Take(MaxCells).ToListAsync(ctx.CancellationToken);
        if (cells.Count == 0)
            return [];

        var ids = cells.Select(c => c.Id).ToArray();
        var users = await (from m in db.CommunityLeagueMembers
                           join p in db.Players on m.Pid equals p.Pid
                           where m.CompeId == compeId && ids.Contains(m.CellId)
                           select new { m.CellId, Player = p }).AsNoTracking().ToListAsync(ctx.CancellationToken);
        var online = OnlinePids(ctx);

        return cells.Select(c =>
        {
            var inCell = users.Where(u => u.CellId == c.Id).Select(u => u.Player).ToList();
            return new KvMessage()
                .Set("cell_id", c.Id)
                .Set("team_id", c.TeamId)
                .Set("entry_user_count", inCell.Count)
                .Set("entry_user_info", inCell.Select(p => PlayerBlock(ctx, p, online.Contains(p.Pid))).ToList());
        }).ToList();
    }

    /// <summary>The cells of the league the player has open (request: none).</summary>
    [Command("CMD_GET_COMMU_LEAGUEENTRYCELLINFO", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueCells(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx);
        if (league is null)
            return [error!];

        return [ctx.Ok().SetList("cell_info_num", "clist", await CellEntriesAsync(ctx, league.Id))];
    }

    /// <summary>
    /// Enters a cell of the open league; a cell_id of 0 opens a new one. Leaving the old cell empties
    /// it, and an empty cell goes. The reply is the cell entered (<c>joined_cell_id</c>, FUN_00523e80).
    /// </summary>
    [Command("CMD_ENTRY_COMMU_LEAGUECELL", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> EnterLeagueCell(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx);
        if (league is null)
            return [error!];

        var pid = ctx.Session.Pid;
        var member = await db.CommunityLeagueMembers.FirstOrDefaultAsync(m => m.CompeId == league.Id && m.Pid == pid, ctx.CancellationToken);
        if (member is null)
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        var wanted = ctx.Request.GetInt32("cell_id", 0);

        // A player who already has a cell keeps it (and its team) instead of opening a second one.
        if (wanted == 0 && member.CellId != 0
            && await db.CommunityLeagueCells.AnyAsync(c => c.Id == member.CellId && c.CompeId == league.Id, ctx.CancellationToken))
            return [ctx.Ok().Set("joined_cell_id", member.CellId)];

        CommunityLeagueCell? cell;
        if (wanted > 0)
        {
            cell = await db.CommunityLeagueCells.FirstOrDefaultAsync(c => c.Id == wanted && c.CompeId == league.Id, ctx.CancellationToken);
            if (cell is null)
                return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];
            if (member.CellId != cell.Id
                && await db.CommunityLeagueMembers.CountAsync(m => m.CompeId == league.Id && m.CellId == cell.Id, ctx.CancellationToken) >= MaxCellUsers)
                return [ctx.Fail("ERR_ENTRYISFULL")];
        }
        else
        {
            if (await db.CommunityLeagueCells.CountAsync(c => c.CompeId == league.Id, ctx.CancellationToken) >= MaxCells)
                return [ctx.Fail("ERR_ENTRYISFULL")];
            cell = new CommunityLeagueCell { CompeId = league.Id };
            db.CommunityLeagueCells.Add(cell);
            await db.SaveChangesAsync(ctx.CancellationToken);
        }

        var previous = member.CellId;
        member.CellId = cell.Id;
        await db.SaveChangesAsync(ctx.CancellationToken);
        if (previous != 0 && previous != cell.Id)
            await DropEmptyCellAsync(previous, ctx.CancellationToken);

        return [ctx.Ok().Set("joined_cell_id", cell.Id)];
    }

    private async Task DropEmptyCellAsync(int cellId, CancellationToken ct)
    {
        if (cellId == 0 || await db.CommunityLeagueMembers.AnyAsync(m => m.CellId == cellId, ct))
            return;

        db.CommunityLeagueCells.RemoveRange(db.CommunityLeagueCells.Where(c => c.Id == cellId));
        db.CommunityLeagueMatches.RemoveRange(db.CommunityLeagueMatches.Where(m => !m.Finished && (m.HomeCellId == cellId || m.AwayCellId == cellId)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The client sends this when the player backs out of the league menu (client-confirmed), so it
    /// only closes the league on their side. Their entry, cell and team stay; the chairman or the
    /// community owner removes members (CMD_DEL_COMMU_LEAGUEMEMBER).
    /// </summary>
    [Command("CMD_LEAVE_COMMU_LEAGUE", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> LeaveLeague(CommandContext ctx)
    {
        // The command has one error: ERR_PLAYERNOTINCOMPE, whichever way the player is not in one.
        if (ctx.Session.CurrentCompeId is not { } compeId)
            return [ctx.Fail("ERR_PLAYERNOTINCOMPE")];

        ctx.Session.CurrentCompeId = null;
        if (ctx.Session.CurrentCommuId is { } commuId)
        {
            await PushLeagueMemberAsync(ctx, commuId, compeId, ctx.Session.Pid);
            await PushMemberAsync(ctx, commuId, ctx.Session.Pid);
        }
        return [ctx.Ok()];
    }

    private async Task RemoveFromLeagueAsync(CommunityLeagueMember member, CancellationToken ct)
    {
        db.CommunityLeagueMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await DropEmptyCellAsync(member.CellId, ct);
    }

    /// <summary>Removing someone from the league: only the player who opened it or the community owner may.</summary>
    [Command("CMD_DEL_COMMU_LEAGUEMEMBER", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> RemoveLeagueMember(CommandContext ctx)
    {
        var (league, _) = await LeagueAsync(ctx);
        if (league is null)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        if (!await MayRunAsync(ctx, league))
            return [ctx.Fail("ERR_NOTCOMMUOWNER")];

        var pid = ctx.Request.GetInt32("pid", 0);
        var member = await db.CommunityLeagueMembers.FirstOrDefaultAsync(m => m.CompeId == league.Id && m.Pid == pid, ctx.CancellationToken);
        if (member is null)
            return [ctx.Fail("ERR_NOTENTRY")];

        await RemoveFromLeagueAsync(member, ctx.CancellationToken);
        await PushLeagueMemberAsync(ctx, league.CommuId, league.Id, pid, "DEL");
        return [ctx.Ok()];
    }

    /// <summary>Closes a league; it moves to the past leagues.</summary>
    [Command("CMD_END_COMMU_LEAGUE", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> EndLeague(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx, ctx.Request.GetInt32("compe_id", 0));
        if (league is null)
            return [error!];

        if (!await MayRunAsync(ctx, league))
            return [ctx.Fail("ERR_NOTCOMMUOWNER")];

        league.Finished = true;
        league.FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await db.SaveChangesAsync(ctx.CancellationToken);
        if (ctx.Session.CurrentCompeId == league.Id)
            ctx.Session.CurrentCompeId = null;
        return [ctx.Ok()];
    }

    /// <summary>Who may end a league or remove its members: whoever opened it, and the community's owner.</summary>
    private async Task<bool> MayRunAsync(CommandContext ctx, CommunityLeague league)
        => league.ChairmanPid == ctx.Session.Pid
           || await db.Communities.AnyAsync(c => c.Id == league.CommuId && c.OwnerPid == ctx.Session.Pid, ctx.CancellationToken);

    /// <summary>
    /// The team the player's cell plays with: the picked team and the footballers' details. The
    /// client's <c>member</c> list is kept as sent and played back by <see cref="GetLeagueTeam"/>,
    /// since both ends read the same keys.
    /// </summary>
    [Command("CMD_SET_COMMU_TEAM", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> SetLeagueTeam(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx);
        if (league is null)
            return [error!];

        var pid = ctx.Session.Pid;
        var member = await db.CommunityLeagueMembers.FirstOrDefaultAsync(m => m.CompeId == league.Id && m.Pid == pid, ctx.CancellationToken);
        var cell = member is { CellId: > 0 }
            ? await db.CommunityLeagueCells.FirstOrDefaultAsync(c => c.Id == member.CellId, ctx.CancellationToken)
            : null;
        if (cell is null)
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        cell.TeamId = ctx.Request.GetInt32("team_id", 0);
        cell.TeamMembers = new KvWriter().Write(new KvMessage().Set("member", ctx.Request.GetValue("member") ?? new List<KvMessage>())).TrimEnd('\0');
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>A cell's team (request: cell_id); the reply is FUN_00529070's team_id, cell_id, member_num and member.</summary>
    [Command("CMD_GET_COMMU_TEAM", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueTeam(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx);
        if (league is null)
            return [error!];

        var id = ctx.Request.GetInt32("cell_id", 0);
        var cell = await db.CommunityLeagueCells.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CompeId == league.Id, ctx.CancellationToken);
        if (cell is null)
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        object members = new List<KvMessage>();
        if (cell.TeamMembers.Length > 0 && new KvReader().TryParse(cell.TeamMembers)?.GetValue("member") is { } stored)
            members = stored;

        if (members is IReadOnlyList<KvMessage> squad)
            members = await WithSquadStatsAsync(league.Id, cell.Id, squad, ctx.CancellationToken);

        var count = members is IReadOnlyList<KvMessage> list ? list.Count : 0;
        return [ctx.Ok().Set("team_id", cell.TeamId).Set("cell_id", cell.Id).Set("member_num", count).Set("member", members)];
    }

    /// <summary>
    /// Fills each squad member's league record: <c>goal</c>, <c>assist</c>, <c>total_yellow_card</c>
    /// and <c>total_red_card</c>, counted from the events of the league matches the cell's players
    /// have played. The squad itself is the client's (it saves the counters as zeros), so these
    /// are worked out here and laid over it, matched on <c>gameplayer_id</c>.
    /// </summary>
    private async Task<IReadOnlyList<KvMessage>> WithSquadStatsAsync(int compeId, int cellId, IReadOnlyList<KvMessage> squad, CancellationToken ct)
    {
        var recordIds = await db.CommunityLeagueMatches.AsNoTracking()
            .Where(m => m.CompeId == compeId && m.Finished && m.MatchRecordId != 0 && (m.HomeCellId == cellId || m.AwayCellId == cellId))
            .Select(m => m.MatchRecordId).ToListAsync(ct);
        if (recordIds.Count == 0)
            return squad;

        var pids = await db.CommunityLeagueMembers.AsNoTracking().Where(m => m.CellId == cellId).Select(m => m.Pid).ToListAsync(ct);
        var played = await db.Matches.AsNoTracking().Where(m => recordIds.Contains(m.Id)).Select(m => new { m.HomePid, m.Events }).ToListAsync(ct);

        // The cell's side in each match.
        return CommunityStats.WithTotals(squad, played.Select(m => (pids.Contains(m.HomePid) ? "HOME" : "AWAY", m.Events)));
    }

    /// <summary>The members of a league (the league twin of the community member list, FUN_00549fb0).</summary>
    [Command("CMD_REQWATCHL_COMMU_COMPEMEMBERLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WatchLeagueMemberList(CommandContext ctx)
    {
        var commuId = ctx.Request.GetInt32("commu_id", ctx.Session.CurrentCommuId ?? 0);
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commuId, ctx.CancellationToken);
        if (community is null)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var compeId = ctx.Request.GetInt32("compe_id", ctx.Session.CurrentCompeId ?? 0);
        if (!await db.CommunityLeagues.AnyAsync(l => l.Id == compeId && l.CommuId == commuId, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        var players = await (from m in db.CommunityLeagueMembers.Where(m => m.CompeId == compeId)
                             join p in db.Players on m.Pid equals p.Pid
                             orderby m.Pid
                             select p).Take(MaxMembers).AsNoTracking().ToListAsync(ctx.CancellationToken);
        var online = OnlinePids(ctx);

        ctx.Session.LeagueMemberWatch = Subscribe(ctx);
        return [ctx.Ok()
            .Set("list_type", "ADD")
            .SetList(ctx.Request.GetString("listnum_name") ?? "member_list_num", ctx.Request.GetString("list_name") ?? "member_list",
                players.Select((p, i) => MemberEntry(ctx, p, online.Contains(p.Pid), community, i, compeId)).ToList())];
    }

    /// <summary>How many entries the client takes in a ranking (32 for members, 20 for teams) and in the team list (400).</summary>
    private const int MemberRankingSize = 32, TeamRankingSize = 20, MaxTeams = 400;

    /// <summary>The finished matches played for the community the player has open, or the error to send.</summary>
    private async Task<(List<MatchRecord>? Matches, KvMessage? Error)> CommunityMatchesAsync(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return (null, ctx.Fail("ERR_COMMUNOTEXIST"));

        return (await db.Matches.AsNoTracking().Where(m => m.CommuId == commuId && m.Completed).ToListAsync(ctx.CancellationToken), null);
    }

    /// <summary>The teams the community's matches were played with (a plain list of ids, FUN_0052d630).</summary>
    [Command("CMD_GET_COMMU_TEAMLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetTeamList(CommandContext ctx)
    {
        var (matches, error) = await CommunityMatchesAsync(ctx);
        if (matches is null)
            return [error!];

        var teams = matches.SelectMany(m => new[] { m.HomeTeamId, m.AwayTeamId }).Where(t => t > 0).Distinct().Order().Take(MaxTeams).ToList();
        return [ctx.Ok().Set("list_num", teams.Count).Set("team_list", new KvArray(teams.Cast<object?>().ToArray()))];
    }

    /// <summary>The community's finished leagues, newest first (at most 20; FUN_005542d0 reads each entry).</summary>
    [Command("CMD_GET_COMMU_PASTLEAGUELIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetPastLeagueList(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var leagues = await db.CommunityLeagues.AsNoTracking()
            .Where(l => l.CommuId == commuId && l.Finished).OrderByDescending(l => l.FinishedAt).Take(MaxCells)
            .ToListAsync(ctx.CancellationToken);
        var pids = leagues.Select(l => l.ChairmanPid).Distinct().ToArray();
        var chairmen = await db.Players.AsNoTracking().Where(p => pids.Contains(p.Pid)).ToDictionaryAsync(p => p.Pid, ctx.CancellationToken);
        var online = OnlinePids(ctx);

        var entries = new List<KvMessage>();
        foreach (var l in leagues.Where(l => chairmen.ContainsKey(l.ChairmanPid)))
            entries.Add(new KvMessage()
                .Set("compe_id", l.Id)
                .Set("compe_name", l.Name)
                .Set("opening_date", l.CreatedAt)
                .Set("finish_date", l.FinishedAt)
                .Set("user_count", await db.CommunityLeagueMembers.CountAsync(m => m.CompeId == l.Id, ctx.CancellationToken))
                .Set("compe_chairman_info", new List<KvMessage> { PlayerBlock(ctx, chairmen[l.ChairmanPid], online.Contains(l.ChairmanPid)) }));

        return [ctx.Ok().SetList("list_num", "rlist", entries)];
    }

    /// <summary>A player's record in the community (FUN_00555ff0): the totals and eight total/average pairs.</summary>
    [Command("CMD_GET_COMMU_MEMBERSRESULTS", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetMembersResults(CommandContext ctx)
    {
        var (matches, error) = await CommunityMatchesAsync(ctx);
        if (matches is null)
            return [error!];

        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var t = CommunityStats.Of(matches, m => m.HomePid == pid ? true : m.AwayPid == pid ? false : null);
        return [ctx.Ok().Set("stats", new List<KvMessage> { Record(t) })];
    }

    /// <summary>
    /// A record entry as FUN_00555ff0 reads it: every field is required, and a missing one fails the
    /// reply. League and cup wins and the combination records are not kept.
    /// </summary>
    private static KvMessage Record(Tally t)
    {
        var stats = new KvMessage()
            .Set("match_count", t.Matches).Set("win_count", t.Wins).Set("lose_count", t.Losses).Set("draw_count", t.Draws)
            .Set("win_average", t.WinPercent).Set("possession_average", t.PossessionAverage)
            .Set("league_win_count", 0).Set("cup_win_count", 0)
            .Set("continuous_win_current", t.WinRun).Set("continuous_win_max", t.WinRunMax)
            .Set("continuous_lose_current", t.LoseRun).Set("continuous_lose_max", t.LoseRunMax)
            .Set("best_combination_max", 0).Set("total_combination_max", 0);
        Totals(stats, t, ("score_get", t.GoalsFor), ("score_lost", t.GoalsAgainst), ("assist", t.Assists), ("foul", t.Fouls),
            ("yellow_card", t.Yellow), ("red_card", t.Red), ("shot", t.Shots), ("suffer_shot", t.ShotsAgainst));
        return stats;
    }

    /// <summary>A team's record (request: team_id): the same entry as a player's (FUN_0052cb40 hands it to FUN_00555ff0 too).</summary>
    [Command("CMD_GET_COMMU_TEAMRESULTS", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetTeamResults(CommandContext ctx)
    {
        var (matches, error) = await CommunityMatchesAsync(ctx);
        if (matches is null)
            return [error!];

        var team = ctx.Request.GetInt32("team_id", 0);
        var t = CommunityStats.Of(matches, m => m.HomeTeamId == team ? true : m.AwayTeamId == team ? false : null);
        return [ctx.Ok().Set("stats", new List<KvMessage> { Record(t) })];
    }

    /// <summary>Each of these is a one-entry list of {total_count, average} (FUN_00552280).</summary>
    private static void Totals(KvMessage stats, Tally t, params (string Key, int Total)[] totals)
    {
        foreach (var (key, total) in totals)
            stats.Set(key, new List<KvMessage> { new KvMessage().Set("total_count", total).Set("average", t.Average(total)) });
    }

    /// <summary>The members ranked by one figure (FUN_005533f0): rank, value and the player block.</summary>
    [Command("CMD_GET_COMMU_MEMBERRANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetMemberRanking(CommandContext ctx)
    {
        var (matches, error) = await CommunityMatchesAsync(ctx);
        if (matches is null)
            return [error!];

        Func<Tally, int> measure = ctx.Request.GetString("ranking_type") switch
        {
            "USER_RANK_CNT_MATCH" => t => t.Matches,
            "USER_RANK_CNT_WIN" => t => t.Wins,
            "USER_RANK_CNT_LOSE" => t => t.Losses,
            "USER_RANK_CNT_DRAW" => t => t.Draws,
            "USER_RANK_AVR_WIN" => t => t.WinPercent,
            "USER_RANK_AVR_SCORE" => t => t.Average(t.GoalsFor),
            "USER_RANK_AVR_LOST" => t => t.Average(t.GoalsAgainst),
            "USER_RANK_AVR_ASSIST" => t => t.Average(t.Assists),
            "USER_RANK_AVR_FOUL" => t => t.Average(t.Fouls),
            "USER_RANK_AVR_YC" => t => t.Average(t.Yellow),
            "USER_RANK_AVR_RC" => t => t.Average(t.Red),
            "USER_RANK_AVR_SHOT" => t => t.Average(t.Shots),
            "USER_RANK_AVR_SHOTED" => t => t.Average(t.ShotsAgainst),
            "USER_RANK_AVR_POSSESSION" => t => t.PossessionAverage,
            "USER_RANK_CONTINUOUS_WIN" => t => t.WinRunMax,
            "USER_RANK_CONTINUOUS_LOSE" => t => t.LoseRunMax,
            _ => _ => 0, // league wins and the combination records are not kept
        };

        var commuId = ctx.Session.CurrentCommuId;
        var players = await (from m in db.CommunityMembers.Where(m => m.CommuId == commuId)
                             join p in db.Players on m.Pid equals p.Pid
                             select p).AsNoTracking().ToListAsync(ctx.CancellationToken);
        var online = OnlinePids(ctx);

        var ranked = players
            .Select(p => (Player: p, Tally: CommunityStats.Of(matches, m => m.HomePid == p.Pid ? true : m.AwayPid == p.Pid ? false : null)))
            .Where(x => x.Tally.Matches > 0)
            .Select(x => (x.Player, Value: measure(x.Tally)))
            .OrderByDescending(x => x.Value).ThenBy(x => x.Player.Pid)
            .Take(MemberRankingSize).ToList();

        var entries = ranked.Select(x => new KvMessage()
            .Set("disp_rank", 1 + ranked.Count(o => o.Value > x.Value))
            .Set("value", x.Value)
            .Set("member_info", new List<KvMessage> { PlayerBlock(ctx, x.Player, online.Contains(x.Player.Pid)) })).ToList();
        return [Ranking(ctx, entries)];
    }

    /// <summary>The teams ranked by one figure (FUN_00553c20): team, rank and value.</summary>
    [Command("CMD_GET_COMMU_TEAMRANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetTeamRanking(CommandContext ctx)
    {
        var (matches, error) = await CommunityMatchesAsync(ctx);
        if (matches is null)
            return [error!];

        Func<Tally, int> measure = ctx.Request.GetString("ranking_type") switch
        {
            "TEAM_RANK_CNT_MATCH" => t => t.Matches,
            "TEAM_RANK_CNT_WIN" => t => t.Wins,
            "TEAM_RANK_CNT_LOSE" => t => t.Losses,
            "TEAM_RANK_CNT_DRAW" => t => t.Draws,
            "TEAM_RANK_AVR_WIN" => t => t.WinPercent,
            "TEAM_RANK_AVR_SCORE" => t => t.Average(t.GoalsFor),
            "TEAM_RANK_AVR_LOST" => t => t.Average(t.GoalsAgainst),
            "TEAM_RANK_AVR_SHOT" => t => t.Average(t.Shots),
            "TEAM_RANK_AVR_SHOTED" => t => t.Average(t.ShotsAgainst),
            "TEAM_RANK_AVR_POSSESSION" => t => t.PossessionAverage,
            _ => _ => 0,
        };

        var ranked = matches.SelectMany(m => new[] { m.HomeTeamId, m.AwayTeamId }).Where(t => t > 0).Distinct()
            .Select(team => (Team: team, Tally: CommunityStats.Of(matches, m => m.HomeTeamId == team ? true : m.AwayTeamId == team ? false : null)))
            .Select(x => (x.Team, Value: measure(x.Tally)))
            .OrderByDescending(x => x.Value).ThenBy(x => x.Team)
            .Take(TeamRankingSize).ToList();

        var entries = ranked.Select(x => new KvMessage()
            .Set("team_id", x.Team)
            .Set("disp_rank", 1 + ranked.Count(o => o.Value > x.Value))
            .Set("value", x.Value)).ToList();
        return [Ranking(ctx, entries)];
    }

    private static KvMessage Ranking(CommandContext ctx, IReadOnlyList<KvMessage> entries)
        => ctx.Ok().Set("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SetList("data_num", "ranking_list", entries);

    // ---- League matches: the fixtures between cells, and putting one up in a room ----

    /// <summary>
    /// A league's fixtures follow its cells: every pair plays, twice (home and away) when the league
    /// says so. They are created as cells appear and dropped, if unplayed, when a cell goes.
    /// </summary>
    private async Task EnsureFixturesAsync(CommunityLeague league, CancellationToken ct)
    {
        var cells = await db.CommunityLeagueCells.Where(c => c.CompeId == league.Id).Select(c => c.Id).OrderBy(i => i).ToListAsync(ct);
        var have = (await db.CommunityLeagueMatches.Where(m => m.CompeId == league.Id)
            .Select(m => new { m.HomeCellId, m.AwayCellId }).ToListAsync(ct)).Select(m => (m.HomeCellId, m.AwayCellId)).ToHashSet();

        var added = false;
        for (var i = 0; i < cells.Count; i++)
        {
            for (var j = i + 1; j < cells.Count; j++)
            {
                foreach (var (home, away) in league.HomeAndAway ? new[] { (cells[i], cells[j]), (cells[j], cells[i]) } : [(cells[i], cells[j])])
                {
                    if (have.Add((home, away)))
                    {
                        db.CommunityLeagueMatches.Add(new CommunityLeagueMatch { CompeId = league.Id, HomeCellId = home, AwayCellId = away });
                        added = true;
                    }
                }
            }
        }

        if (added)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>The fixture as FUN_0054b940 reads it: the id and the two cells.</summary>
    private async Task<List<(CommunityLeagueMatch Match, KvMessage Entry)>> FixtureEntriesAsync(
        CommandContext ctx, IReadOnlyList<CommunityLeagueMatch> matches)
    {
        var cellsByLeague = new Dictionary<int, Dictionary<int, KvMessage>>();
        var entries = new List<(CommunityLeagueMatch, KvMessage)>();
        foreach (var m in matches)
        {
            if (!cellsByLeague.TryGetValue(m.CompeId, out var cells))
                cellsByLeague[m.CompeId] = cells = (await CellEntriesAsync(ctx, m.CompeId)).ToDictionary(c => c.GetInt32("cell_id"));

            if (cells.TryGetValue(m.HomeCellId, out var home) && cells.TryGetValue(m.AwayCellId, out var away))
                entries.Add((m, new KvMessage()
                    .Set("match_id", m.Id)
                    .Set("home_team_info", new List<KvMessage> { home })
                    .Set("away_team_info", new List<KvMessage> { away })));
        }

        return entries;
    }

    /// <summary>
    /// The fixtures the player's cell can play now (at most 38), in the league they have open: the
    /// client asks as it opens the league menu, and a player who is in an older league as well
    /// would otherwise get both leagues' matches (4 where 2 are right).
    /// </summary>
    [Command("CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WatchPlayableMatches(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } commuId)
            return [ctx.Fail("ERR_COMMUNOTENTRY")];

        var pid = ctx.Session.Pid;
        var open = ctx.Session.CurrentCompeId;
        var leagues = await db.CommunityLeagues
            .Where(l => l.CommuId == commuId && !l.Finished && (open == null || l.Id == open))
            .ToListAsync(ctx.CancellationToken);
        var mine = new List<CommunityLeagueMatch>();
        foreach (var league in leagues)
        {
            var cell = await db.CommunityLeagueMembers.Where(m => m.CompeId == league.Id && m.Pid == pid).Select(m => m.CellId).FirstOrDefaultAsync(ctx.CancellationToken);
            if (cell == 0)
                continue;

            await EnsureFixturesAsync(league, ctx.CancellationToken);
            mine.AddRange(await db.CommunityLeagueMatches.AsNoTracking()
                .Where(m => m.CompeId == league.Id && !m.Finished && (m.HomeCellId == cell || m.AwayCellId == cell))
                .OrderBy(m => m.Id).ToListAsync(ctx.CancellationToken));
        }

        var entries = (await FixtureEntriesAsync(ctx, mine.Take(MaxMatchListEntries).ToList())).Select(e => e.Entry).ToList();
        return [WatchList(ctx, entries)];
    }

    /// <summary>The league's fixtures with their score (the client takes at most 38; the request's list_max is 9).</summary>
    [Command("CMD_REQWATCHL_COMMU_MATCHLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WatchMatches(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx, ctx.Request.GetInt32("compe_id", ctx.Session.CurrentCompeId ?? 0), open: false);
        if (league is null)
            return [error!];

        await EnsureFixturesAsync(league, ctx.CancellationToken);
        var limit = Math.Clamp(ctx.Request.GetInt32("list_max", 9), 1, MaxMatchListEntries);
        var matches = await db.CommunityLeagueMatches.AsNoTracking().Where(m => m.CompeId == league.Id).OrderBy(m => m.Id).Take(limit).ToListAsync(ctx.CancellationToken);

        var entries = (await FixtureEntriesAsync(ctx, matches)).Select(e => new KvMessage()
            .Set("match_status", e.Match.Finished ? "END" : "ENTRY")
            .Set("match", new List<KvMessage> { e.Entry })
            .Set("home_goal", new List<KvMessage> { Score(e.Match.HomePeriods) })
            .Set("away_goal", new List<KvMessage> { Score(e.Match.AwayPeriods) })).ToList();
        return [WatchList(ctx, entries, "match_list_num", "match_list")];
    }

    private const int MaxMatchListEntries = 38;

    private static KvMessage WatchList(CommandContext ctx, IReadOnlyList<KvMessage> entries, string numKey = "match_list_num", string listKey = "match_list")
        => ctx.Ok().Set("list_type", "ADD")
            .SetList(ctx.Request.GetString("listnum_name") ?? numKey, ctx.Request.GetString("list_name") ?? listKey, entries);

    /// <summary>Goals per period as FUN_0054c1f0 reads them.</summary>
    private static KvMessage Score(string periods)
    {
        var p = MatchSheet.Parse(periods);
        return new KvMessage().Set("score_1st", p[0]).Set("score_2nd", p[1]).Set("score_ex1st", p[2]).Set("score_ex2nd", p[3]).Set("score_pk", p[4]);
    }

    /// <summary>The player's own pending delivery: the next CMD_WATCH_COMMU_MATCH push is the answer to this.</summary>
    [Command("CMD_WATCH_COMMU_MATCH", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> WatchMatchOffers(CommandContext ctx)
    {
        ctx.Session.CommunityMatchWatchRqid = ctx.Rqid;
        return Reply.None();
    }

    /// <summary>The applicant's side: who has accepted the offer, or that it was refused or cancelled.</summary>
    [Command("CMD_WATCH_COMMU_MATCH_WAITING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> WatchMatchAnswers(CommandContext ctx)
    {
        ctx.Session.CommunityMatchWaitingRqid = ctx.Rqid;
        return Reply.None();
    }

    /// <summary>
    /// Puts a fixture up in the player's room. The other cell's online members are told through
    /// their CMD_WATCH_COMMU_MATCH (compe_id, match_id, from_pid, room_id; FUN_005283e0).
    /// </summary>
    [Command("CMD_APPLY_COMMU_MATCH", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> ApplyMatch(CommandContext ctx)
    {
        var matchId = ctx.Request.GetInt32("match_id", 0);
        var roomId = ctx.Request.GetInt32("room_id", 0);
        var fixture = await db.CommunityLeagueMatches.FirstOrDefaultAsync(m => m.Id == matchId && !m.Finished, ctx.CancellationToken);
        if (fixture is null)
            return [ctx.Fail("ERR_MATCHNOTEXIST")];

        var league = await db.CommunityLeagues.AsNoTracking().FirstOrDefaultAsync(l => l.Id == fixture.CompeId && !l.Finished, ctx.CancellationToken);
        if (league is null)
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];
        if (ctx.Session.CurrentCommuId != league.CommuId)
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var pid = ctx.Session.Pid;
        var cell = await db.CommunityLeagueMembers.Where(m => m.CompeId == league.Id && m.Pid == pid).Select(m => m.CellId).FirstOrDefaultAsync(ctx.CancellationToken);
        if (cell == 0 || (cell != fixture.HomeCellId && cell != fixture.AwayCellId))
            return [ctx.Fail("ERR_PLAYERNOTINCOMPE")];

        var room = ctx.Hub.FindRoom(roomId);
        if (room is null || room.OwnerPid != pid)
            return [ctx.Fail("ERR_DATABASE")];

        var other = cell == fixture.HomeCellId ? fixture.AwayCellId : fixture.HomeCellId;
        var opponents = await db.CommunityLeagueMembers.Where(m => m.CompeId == league.Id && m.CellId == other).Select(m => m.Pid).ToListAsync(ctx.CancellationToken);
        // The opponent only has to be around, in this community: the client was never seen to open
        // CMD_WATCH_COMMU_MATCH (not once in a full two-player league session), so requiring one
        // refused every challenge. The offer goes to a parked watch when there is one, otherwise
        // under rqid 1 like unsolicited chat.
        var reachable = opponents.SelectMany(p => ctx.Hub.SessionsForPid(p)).Where(s => s.CurrentCommuId == league.CommuId).ToList();
        if (reachable.Count == 0)
            return [ctx.Fail("ERR_COMMUMATCHREFUSED")];

        room.LeagueMatchId = matchId;
        ctx.Hub.CommunityOffers[matchId] = new CommunityMatchOffer(matchId, league.Id, pid, roomId);
        foreach (var s in reachable)
            s.Push(KvMessage.Ok("CMD_WATCH_COMMU_MATCH", s.CommunityMatchWatchRqid ?? Hub.UnsolicitedTextChatRqid)
                .Set("compe_id", league.Id).Set("match_id", matchId).Set("from_pid", pid).Set("room_id", roomId));

        log.LogInformation("Player {Pid} offered league match {Match} (room {Room}) to {Count} connection(s) of the other cell", pid, matchId, roomId, reachable.Count);
        return [ctx.Ok()];
    }

    /// <summary>The other cell's answer to an offer; yes puts the player on the applicant's waiting list.</summary>
    [Command("CMD_REPLY_COMMU_MATCH", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> ReplyMatch(CommandContext ctx)
    {
        var matchId = ctx.Request.GetInt32("match_id", 0);
        if (!ctx.Hub.CommunityOffers.TryGetValue(matchId, out var offer))
            return [ctx.Fail("ERR_MATCHNOTEXIST")];
        if (offer.Canceled)
            return [ctx.Fail("ERR_COMMUMATCHCANCELED")];

        var pid = ctx.Session.Pid;
        var fixture = await db.CommunityLeagueMatches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchId, ctx.CancellationToken);
        if (fixture is null or { Finished: true } || ctx.Hub.FindRoom(offer.RoomId) is null)
        {
            ctx.Hub.CommunityOffers.TryRemove(matchId, out _);
            return [ctx.Fail("ERR_COMMUMATCHCANCELED")];
        }

        var cell = fixture is null ? 0 : await db.CommunityLeagueMembers.Where(m => m.CompeId == offer.CompeId && m.Pid == pid).Select(m => m.CellId).FirstOrDefaultAsync(ctx.CancellationToken);
        if (fixture is null || cell == 0 || (cell != fixture.HomeCellId && cell != fixture.AwayCellId) || pid == offer.ApplicantPid)
            return [ctx.Fail("ERR_COMMUCOMPENOTEXIST")];

        if (ctx.Request.GetString("reply") == "YES")
        {
            offer.Accepted.Add(pid);

            // Both waiting screens (the applicant's and every accepter's) draw each side from this
            // list, the first player of a side being the one shown: the applicant, then who said yes.
            var audience = offer.Accepted.Prepend(offer.ApplicantPid).Distinct().ToList();
            var found = await db.Players.AsNoTracking().Where(p => audience.Contains(p.Pid)).ToListAsync(ctx.CancellationToken);
            var players = audience.Select(a => found.FirstOrDefault(p => p.Pid == a)).OfType<Player>().ToList();
            var online = OnlinePids(ctx);
            var list = players.Select(p => PlayerBlock(ctx, p, online.Contains(p.Pid))).ToList();
            foreach (var who in audience)
                Tell(ctx.Hub, who, rqid => KvMessage.Ok("CMD_WATCH_COMMU_MATCH_WAITING", rqid).SetList("player_num", "player_list", list));
        }
        else
        {
            // The waiting watch lists only ERR_COMMUMATCHCANCELED among its errors; an error outside
            // a watch's vocabulary can leave that module dead for the session (see TextChatHandlers).
            Tell(ctx.Hub, offer.ApplicantPid, rqid => KvMessage.Fail("CMD_WATCH_COMMU_MATCH_WAITING", rqid, "ERR_COMMUMATCHCANCELED"));
        }

        return [ctx.Ok()];
    }

    /// <summary>The applicant withdraws the offer; whoever had said yes is told it was cancelled.</summary>
    [Command("CMD_CANCEL_COMMU_MATCH", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public ValueTask<KvMessage[]> CancelMatch(CommandContext ctx)
    {
        var matchId = ctx.Request.GetInt32("match_id", 0);
        if (!ctx.Hub.CommunityOffers.TryGetValue(matchId, out var offer))
            return Reply.Of(ctx.Fail("ERR_COMMUCOMPENOTEXIST"));
        if (offer.ApplicantPid != ctx.Session.Pid)
            return Reply.Of(ctx.Fail("ERR_PLAYERISNOTGAMER"));

        offer.Canceled = true;
        ctx.Hub.CommunityOffers.TryRemove(matchId, out _);
        if (ctx.Hub.FindRoom(offer.RoomId) is { } room && room.LeagueMatchId == matchId)
            room.LeagueMatchId = 0;
        foreach (var accepted in offer.Accepted)
            Tell(ctx.Hub, accepted, rqid => KvMessage.Fail("CMD_WATCH_COMMU_MATCH_WAITING", rqid, "ERR_COMMUMATCHCANCELED"));

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// Delivers one waiting-list push to the player: to their parked watch if they have one,
    /// otherwise under rqid 1. The client never sends CMD_WATCH_COMMU_MATCH_WAITING (it is not seen
    /// on the wire, like the chat watch), so pushes must not depend on it.
    /// </summary>
    private static void Tell(Hub hub, int pid, Func<int, KvMessage> message)
    {
        foreach (var s in hub.SessionsForPid(pid).Where(s => s.Role == ServiceRole.Menu))
            s.Push(message(s.CommunityMatchWaitingRqid ?? Hub.UnsolicitedTextChatRqid));
    }

    // ---- League tables: worked out from the fixtures played ----

    /// <summary>The league table as FUN_0054d1c0 reads each line: the points and the cell.</summary>
    private async Task<List<KvMessage>> TableEntriesAsync(CommandContext ctx, int compeId)
    {
        var cells = (await CellEntriesAsync(ctx, compeId)).ToDictionary(c => c.GetInt32("cell_id"));
        var finished = await db.CommunityLeagueMatches.AsNoTracking().Where(m => m.CompeId == compeId && m.Finished).ToListAsync(ctx.CancellationToken);
        var table = CommunityStats.Table(cells.Keys, finished);
        return table.Select(line => new KvMessage()
            .Set("team_rank_disp", 1 + table.Count(o => (o.Points, o.Difference, o.For).CompareTo((line.Points, line.Difference, line.For)) > 0))
            .Set("team_rank_point", line.Points)
            .Set("team_rank_win", line.Won)
            .Set("team_rank_draw", line.Drawn)
            .Set("team_rank_lose", line.Lost)
            .Set("team_rank_score", line.For)
            .Set("team_rank_lost", line.Against)
            .Set("team_rank_scorediff", line.Difference)
            .Set("entry_info", new List<KvMessage> { cells[line.CellId] })).ToList();
    }

    /// <summary>The open league's table (the client takes at most 20 lines).</summary>
    [Command("CMD_GET_COMMU_COMPERANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueRanking(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx, open: false);
        if (league is null)
            return [error!];

        return [ctx.Ok().Set("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SetList("info_num", "i_list", await TableEntriesAsync(ctx, league.Id))];
    }

    /// <summary>A finished league's final table (request: compe_id).</summary>
    [Command("CMD_GET_COMMU_PASTLEAGUERESULT", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetPastLeagueResult(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx, ctx.Request.GetInt32("compe_id", 0), open: false);
        if (league is null)
            return [error!];

        return [ctx.Ok().SetList("info_num", "ilist", await TableEntriesAsync(ctx, league.Id))];
    }

    /// <summary>Every fixture of the open league with its score (the client takes at most 380; FUN_0054dac0).</summary>
    [Command("CMD_GET_COMMU_COMPERESULT", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueResult(CommandContext ctx)
    {
        var (league, error) = await LeagueAsync(ctx, open: false);
        if (league is null)
            return [error!];

        await EnsureFixturesAsync(league, ctx.CancellationToken);
        var fixtures = await db.CommunityLeagueMatches.AsNoTracking().Where(m => m.CompeId == league.Id).OrderBy(m => m.Id).Take(380).ToListAsync(ctx.CancellationToken);
        var entries = fixtures.Select(m => new KvMessage()
            .Set("played", m.Finished)
            .Set("home_id", m.HomeCellId)
            .Set("home_pts", m.HomeGoals)
            .Set("away_id", m.AwayCellId)
            .Set("away_pts", m.AwayGoals)).ToList();
        return [ctx.Ok().SetList("info_num", "mlist", entries)];
    }

    /// <summary>Cells ranked by the goals they scored in the league (request: compe_id; FUN_0054c990).</summary>
    [Command("CMD_GET_COMMU_GOALRANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueGoalRanking(CommandContext ctx)
        => await CellRankingAsync(ctx, "goal_ranking", (cell, fixtures, _) =>
            fixtures.Where(m => m.HomeCellId == cell).Sum(m => m.HomeGoals) + fixtures.Where(m => m.AwayCellId == cell).Sum(m => m.AwayGoals));

    /// <summary>Cells ranked by the goals they set up, from the goal reports of the fixtures played.</summary>
    [Command("CMD_GET_COMMU_ASSISTRANKING", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> GetLeagueAssistRanking(CommandContext ctx)
        => await CellRankingAsync(ctx, "assist_ranking", (cell, fixtures, records) => fixtures
            .Where(m => m.MatchRecordId != 0 && records.ContainsKey(m.MatchRecordId))
            .Sum(m => CommunityStats.Of([records[m.MatchRecordId]], r => SideOf(cell, m, r)).Assists));

    /// <summary>True when the cell was the record's home side. The record's home is whoever the lineup put there, not the fixture's home cell.</summary>
    private bool? SideOf(int cell, CommunityLeagueMatch fixture, MatchRecord record)
    {
        if (cell != fixture.HomeCellId && cell != fixture.AwayCellId)
            return null;

        var homeCell = db.CommunityLeagueMembers.Where(m => m.CompeId == fixture.CompeId && m.Pid == record.HomePid).Select(m => m.CellId).FirstOrDefault();
        var swapped = homeCell != fixture.HomeCellId;
        return cell == fixture.HomeCellId ? !swapped : swapped;
    }

    private async ValueTask<KvMessage[]> CellRankingAsync(CommandContext ctx, string listKey,
        Func<int, List<CommunityLeagueMatch>, Dictionary<long, MatchRecord>, int> measure)
    {
        var (league, error) = await LeagueAsync(ctx, ctx.Request.GetInt32("compe_id", ctx.Session.CurrentCompeId ?? 0), open: false);
        if (league is null)
            return [error!];

        var cells = (await CellEntriesAsync(ctx, league.Id)).ToDictionary(c => c.GetInt32("cell_id"));
        var fixtures = await db.CommunityLeagueMatches.AsNoTracking().Where(m => m.CompeId == league.Id && m.Finished).ToListAsync(ctx.CancellationToken);
        var recordIds = fixtures.Where(m => m.MatchRecordId != 0).Select(m => m.MatchRecordId).ToList();
        var records = await db.Matches.AsNoTracking().Where(m => recordIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ctx.CancellationToken);

        var ranked = cells.Keys.Select(cell => (Cell: cell, Count: measure(cell, fixtures, records)))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Cell).Take(20).ToList();
        var entries = ranked.Select(x => new KvMessage()
            .Set("disp_rank", 1 + ranked.Count(o => o.Count > x.Count))
            .Set("ga_count", x.Count)
            .Set("entry_info", new List<KvMessage> { cells[x.Cell] })).ToList();
        return [ctx.Ok().Set("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()).SetList("data_num", listKey, entries)];
    }

    /// <summary>The community the player has open, when they own it; otherwise the error to send.</summary>
    private async Task<(Community? Community, KvMessage? Error)> OwnedCurrentAsync(CommandContext ctx)
    {
        if (ctx.Session.CurrentCommuId is not { } id)
            return (null, ctx.Fail("ERR_COMMUNOTEXIST"));

        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == id, ctx.CancellationToken);
        if (community is null)
            return (null, ctx.Fail("ERR_COMMUNOTEXIST"));

        return community.OwnerPid == ctx.Session.Pid ? (community, null) : (null, ctx.Fail("ERR_NOTCOMMUOWNER"));
    }

    /// <summary>
    /// A community's members (FUN_00549fb0 reads each entry). The request is a watch: members who
    /// join, leave, or open or close the community afterwards reach the open screen as deltas
    /// (a member whose connection simply drops is not pushed yet).
    /// </summary>
    [Command("CMD_REQWATCHL_COMMU_MEMBERLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Modern)]
    public async ValueTask<KvMessage[]> WatchMemberList(CommandContext ctx)
    {
        var id = ctx.Request.GetInt32("commu_id", 0);
        if (!await db.Communities.AnyAsync(c => c.Id == id, ctx.CancellationToken))
            return [ctx.Fail("ERR_COMMUNOTEXIST")];

        var community = await db.Communities.AsNoTracking().FirstAsync(c => c.Id == id, ctx.CancellationToken);
        var members = await (from m in db.CommunityMembers.Where(m => m.CommuId == id)
                             join p in db.Players on m.Pid equals p.Pid
                             orderby m.JoinedAt, m.Pid
                             select p).Take(MaxMembers).AsNoTracking().ToListAsync(ctx.CancellationToken);
        var online = OnlinePids(ctx);

        // The client files each entry in a 32-slot array under info.index (FUN_00543d70), so the
        // index is the entry's position; all zeros would pile every member into slot 0.
        var entries = members.Select((p, i) => MemberEntry(ctx, p, online.Contains(p.Pid), community, i)).ToList();

        // The list is a watch: later changes arrive as deltas under this request's rqid.
        ctx.Session.CommunityMemberWatch = Subscribe(ctx);
        return [ctx.Ok()
            .Set("list_type", "ADD")
            .SetList(ctx.Request.GetString("listnum_name") ?? "member_list_num", ctx.Request.GetString("list_name") ?? "member_list", entries)];
    }

    private static WatchListSubscription Subscribe(CommandContext ctx)
        => new(ctx.Request.MsgName ?? "", ctx.Rqid,
            ctx.Request.GetString("listnum_name") ?? "member_list_num",
            ctx.Request.GetString("list_name") ?? "member_list");

    private static KvMessage Delta(WatchListSubscription sub, string listType, KvMessage entry)
        => KvMessage.Ok(sub.Msg, sub.Rqid).Set("list_type", listType).SetList(sub.NumName, sub.ListName, [entry]);

    /// <summary>
    /// Tells the open community member lists about one member: ADD/UPDATE upsert the entry (by
    /// <c>info.pid</c>), DEL removes it. The player's own connection is left out; its screen
    /// reads the list afresh.
    /// </summary>
    private async Task PushMemberAsync(CommandContext ctx, int commuId, int pid, string listType = "UPDATE")
    {
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commuId, ctx.CancellationToken);
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Pid == pid, ctx.CancellationToken);
        if (community is null || player is null)
            return;

        var pids = await db.CommunityMembers.AsNoTracking().Where(m => m.CommuId == commuId)
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.Pid).Select(m => m.Pid).Take(MaxMembers).ToListAsync(ctx.CancellationToken);
        var index = pids.IndexOf(pid);
        if (index < 0 && listType != "DEL")
            return;

        var entry = MemberEntry(ctx, player, OnlinePids(ctx).Contains(pid), community, Math.Max(index, 0));
        foreach (var s in ctx.Hub.Sessions.Where(s => s.Id != ctx.Session.Id && s.CommunityMemberWatch is not null && s.CurrentCommuId == commuId).ToList())
            s.Push(Delta(s.CommunityMemberWatch!, listType, entry));
    }

    /// <summary>The same for the open league's member list.</summary>
    private async Task PushLeagueMemberAsync(CommandContext ctx, int commuId, int compeId, int pid, string listType = "UPDATE")
    {
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == commuId, ctx.CancellationToken);
        var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Pid == pid, ctx.CancellationToken);
        if (community is null || player is null)
            return;

        var pids = await db.CommunityLeagueMembers.AsNoTracking().Where(m => m.CompeId == compeId)
            .OrderBy(m => m.Pid).Select(m => m.Pid).Take(MaxMembers).ToListAsync(ctx.CancellationToken);
        var index = pids.IndexOf(pid);
        if (index < 0 && listType != "DEL")
            return;

        var entry = MemberEntry(ctx, player, OnlinePids(ctx).Contains(pid), community, Math.Max(index, 0), compeId);
        foreach (var s in ctx.Hub.Sessions.Where(s => s.Id != ctx.Session.Id && s.LeagueMemberWatch is not null && s.CurrentCompeId == compeId).ToList())
            s.Push(Delta(s.LeagueMemberWatch!, listType, entry));
    }

    /// <summary>One member of a member list (FUN_00549fb0): where they are, and their player block.</summary>
    private static KvMessage MemberEntry(CommandContext ctx, Player p, bool online, Community community, int index, int? compeId = null)
    {
        // Where the member is: inside this league, inside the community, or just online. The
        // screens count the first two ("1/2" members, "2/2" league participants), and
        // COMMUNITY_COMPE makes the client read a compe_id.
        var here = ctx.Hub.Sessions.Where(s => s.Pid == p.Pid && s.CurrentCommuId == community.Id).ToList();
        // The community list too: the league details screen counts the members whose mode is
        // COMMUNITY_COMPE with that league's compe_id (FUN_005d3eb0).
        compeId ??= here.Select(s => s.CurrentCompeId).FirstOrDefault(c => c is not null);
        var inLeague = compeId is not null && here.Any(s => s.CurrentCompeId == compeId);
        var entry = new KvMessage().Set("mode", inLeague ? "COMMUNITY_COMPE" : here.Count > 0 ? "COMMUNITY" : online ? "NETWORK" : "NONE");
        if (inLeague)
            entry.Set("compe_id", compeId!.Value);
        return MemberEntryBody(entry, ctx, p, online, community, index);
    }

    private static KvMessage MemberEntryBody(KvMessage entry, CommandContext ctx, Player p, bool online, Community community, int index) => entry
        .Set("lobby_name", "")
        .Set("block_name", "")
        .Set("commu_name", community.Name)
        .Set("commu_id", community.Id)
        .Set("room_id", 0)
        .Set("beginnermark", p.BeginnerMark)
        .Set("position_want", p.PositionWant)
        .Set("selfreport_level", p.SelfReportLevel)
        .Set("desired_position", KvArray.Repeat("NO", 13))
        .Set("info", new List<KvMessage> { PlayerBlock(ctx, p, online, index) });

    /// <summary>The session is no longer in a community: it leaves the community's room list and rooms.</summary>
    private static void Close(Hub hub, Session session)
    {
        session.CurrentCommuId = null;
        session.CurrentCompeId = null;
        hub.LeaveCommunityBlock(session);
    }

    private static HashSet<int> OnlinePids(CommandContext ctx)
        => ctx.Hub.Sessions.Where(s => s.State >= SessionState.PlayerSelected).Select(s => s.Pid).ToHashSet();

    /// <summary>The player block of a community entry and of a member entry (FUN_00549460).</summary>
    private static KvMessage PlayerBlock(CommandContext ctx, Player p, bool online, int index = 0) => new KvMessage()
        .Set("pid", p.Pid)
        .Set("name", p.Name)
        .Set("manner", p.Manner)
        .Set("division", RankSystem.Label(p.Division, ctx.Profile))
        .Set("rating", p.Rating)
        .Set("status", online ? "LOGIN" : "LOGOUT")
        .Set("lang", p.Lang)
        .Set("index", index)
        .Set("npid", "");

    /// <summary>Communities the player may see: all but the invite-only ones they are not in.</summary>
    private IQueryable<Community> Visible(int pid)
        => db.Communities.AsNoTracking()
            .Where(c => !c.InviteOnly || db.CommunityMembers.Any(m => m.CommuId == c.Id && m.Pid == pid));

    /// <summary>One community as FUN_0054e980 reads it; a community whose owner is gone is left out.</summary>
    private async Task<List<KvMessage>> EntriesAsync(CommandContext ctx, IReadOnlyList<Community> communities)
    {
        if (communities.Count == 0)
            return [];

        var ids = communities.Select(c => c.Id).ToArray();
        var ownerPids = communities.Select(c => c.OwnerPid).Distinct().ToArray();

        // Members whose player is gone do not count.
        var members = await (from m in db.CommunityMembers
                             join p in db.Players on m.Pid equals p.Pid
                             where ids.Contains(m.CommuId)
                             select new { m.CommuId, m.Pid })
            .AsNoTracking().ToListAsync(ctx.CancellationToken);
        var owners = await db.Players.AsNoTracking()
            .Where(p => ownerPids.Contains(p.Pid))
            .ToDictionaryAsync(p => p.Pid, ctx.CancellationToken);
        var online = OnlinePids(ctx);

        // The community's "Matches Completed" and "Community Leagues Completed" lines.
        var matchCounts = await db.Matches.AsNoTracking()
            .Where(m => m.Completed && ids.Contains(m.CommuId))
            .GroupBy(m => m.CommuId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, ctx.CancellationToken);
        var leagueCounts = await db.CommunityLeagues.AsNoTracking()
            .Where(l => l.Finished && ids.Contains(l.CommuId))
            .GroupBy(l => l.CommuId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Id, g => g.Count, ctx.CancellationToken);

        var entries = new List<KvMessage>();
        foreach (var c in communities)
        {
            if (!owners.TryGetValue(c.OwnerPid, out var owner))
                continue;

            var inCommunity = members.Where(m => m.CommuId == c.Id).ToList();
            var password = c.OwnerPid == ctx.Session.Pid ? c.Password : c.Password.Length > 0 ? PasswordMask : "";
            entries.Add(new KvMessage()
                .Set("commu_id", c.Id)
                .Set("member_count", inCommunity.Count)
                .Set("logon_member_count", inCommunity.Count(m => online.Contains(m.Pid)))
                .Set("total_match_count", matchCounts.GetValueOrDefault(c.Id))
                .Set("total_legends_count", 0)
                .Set("total_compe_count", leagueCounts.GetValueOrDefault(c.Id))
                .Set("update_date", c.CreatedAt)
                .Set("start_time", c.StartTime)
                .Set("end_time", c.EndTime)
                .Set("commu_name", c.Name)
                .Set("commu_description", c.Description)
                .Set("invite_only", c.InviteOnly ? "YES" : "NO")
                .Set("password", password)
                // The client's own lists end with an empty element, so this one does as well.
                .Set("week", new KvArray(c.Week.Select(f => (object?)(f == 'Y' ? "YES" : "NO")).Append(new KvRaw("")).ToArray()))
                .Set("owner_info", new List<KvMessage> { PlayerBlock(ctx, owner, online.Contains(owner.Pid)) }));
        }

        return entries;
    }

    /// <summary>Seven Y/N flags from a <c>week</c> list; the client ends it with an empty element.</summary>
    private static string WeekFlags(object? week)
    {
        var values = (week as KvArray)?.Values ?? [];
        return new string(Enumerable.Range(0, 7).Select(i => i < values.Count && values[i] is "YES" ? 'Y' : 'N').ToArray());
    }

    /// <summary>A search asks for the days it ticked; a community has to play on every one of them.</summary>
    private static bool PlaysOn(Community c, string wanted)
        => !wanted.Where((flag, day) => flag == 'Y' && c.Week[day] != 'Y').Any();

    /// <summary>Enum 0 is HOUR_NONE, then HOUR_00_01 as 1 up to HOUR_23_00 as 24.</summary>
    private static int HourIndex(string? name)
        => name is { Length: >= 7 } && name.StartsWith("HOUR_", StringComparison.Ordinal) && int.TryParse(name.AsSpan(5, 2), out var hour)
            ? hour + 1
            : 0;

    /// <summary>A community that never set its hours is open at any of them.</summary>
    private static bool ActiveAt(Community c, int hour)
        => hour == 0 || (c.StartTime == 0 && c.EndTime == 0) || (c.StartTime <= hour && hour <= c.EndTime);

    private static string Cut(string text, int length) => text.Length <= length ? text : text[..length];

    // ---- Commands only PES 2012 and later send; communities are served, so these answer with the client's own "none" ----

    /// <summary>
    /// The "join a community?" prompt after login. ERR_COMMUNOTEXIST is the client's own
    /// "there is nothing to recommend" answer (its other error is ERR_DATABASE); a plain NOERR
    /// without <c>commu_info</c> made the client offer a community that does not exist.
    /// </summary>
    [Command("CMD_GET_COMMU_INDUCEMENT", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public ValueTask<KvMessage[]> GetCommuInducement(CommandContext ctx)
        => Reply.Of(ctx.Fail("ERR_COMMUNOTEXIST"));

    /// <summary>
    /// A player's community card, asked for after a match (request: pid). Cards are not
    /// served, so the client's own ERR_DATANOTFOUND; a bare NOERR made it suggest a community.
    /// </summary>
    [Command("CMD_GET_COMMU_CARD", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public ValueTask<KvMessage[]> GetCommuCard(CommandContext ctx)
        => Reply.Of(ctx.Fail("ERR_DATANOTFOUND"));

    /// <summary>A player's community cards (request: target_pid; FUN_00640ad0 reads commu_intro_list_num).</summary>
    [Command("CMD_GET_COMMU_CARDLIST", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public ValueTask<KvMessage[]> GetCommuCardList(CommandContext ctx)
        => Reply.Of(ctx.Ok().Set("commu_intro_list_num", 0));

    /// <summary>The client clears its home community with commu_id=0, which is always true here.</summary>
    [Command("CMD_SET_HOME_COMMU", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public ValueTask<KvMessage[]> SetHomeCommu(CommandContext ctx)
        => Reply.Of(ctx.Ok());
}

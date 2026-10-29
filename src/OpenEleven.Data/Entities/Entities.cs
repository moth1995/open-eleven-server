// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.ComponentModel.DataAnnotations;

namespace OpenEleven.Data.Entities;

public sealed class Account
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string GameId { get; set; } = "";

    /// <summary>
    /// The hash the client presents, stored as supplied at registration. It is compared,
    /// never reversed, and is never the plaintext password.
    /// </summary>
    [MaxLength(255)]
    public string PasswordHash { get; set; } = "";

    /// <summary>
    /// Product serial, supplied when the account is registered. The client presents its
    /// own copy as <c>tmpRegcode</c> in MSG_REQAUTH and the two must match. A serial may
    /// be shared by multiple accounts, so it is an extra check and never an account key.
    /// </summary>
    [MaxLength(64)]
    public string? RegCode { get; set; }

    /// <summary>Cleared atomically by the first successful binary authentication.</summary>
    public bool FirstLogin { get; set; } = true;

    public bool Banned { get; set; }

    [MaxLength(255)]
    public string? BanReason { get; set; }

    // Stored as UTC DateTime rather than DateTimeOffset: SQLite cannot ORDER BY or
    // index a DateTimeOffset column, and these are all ordered or filtered on.
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }

    /// <summary>szPatchVersion / szDLContentsVersion the client reported at its last login.</summary>
    [MaxLength(16)]
    public string PatchVersion { get; set; } = "";

    [MaxLength(16)]
    public string DlcVersion { get; set; } = "";

    public List<Player> Players { get; set; } = new();
}

public sealed class Player
{
    public int Pid { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }

    [MaxLength(15)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Invariant uppercase form of <see cref="Name"/> used for global,
    /// case-insensitive uniqueness across database providers.
    /// </summary>
    [MaxLength(64)]
    public string NormalizedName { get; set; } = "";

    /// <summary>Stored in the DivisionIndex column.</summary>
    public Division Division { get; set; }

    [MaxLength(16)]
    public string Kind { get; set; } = "NORMAL";

    [MaxLength(4)]
    public string Lang { get; set; } = "EN";

    [MaxLength(128)]
    public string Intro { get; set; } = "";

    public int Rating { get; set; }
    public int Point { get; set; }
    public int Rank { get; set; }
    public int Manner { get; set; } = 3;
    public int Country { get; set; }
    public int Area { get; set; }
    public int BirthMonth { get; set; }
    public int BirthDay { get; set; }
    public int FavoriteTeam { get; set; }
    public int FavoritePlayer { get; set; }

    [MaxLength(16)]
    public string SelfReportLevel { get; set; } = "PRO";

    [MaxLength(8)]
    public string PositionWant { get; set; } = "CF";

    /// <summary>13 YES/NO flags, stored as a compact bitmask.</summary>
    public int DesiredPositionMask { get; set; }

    public bool AutoMatchWant { get; set; } = true;
    public bool BeginnerMark { get; set; } = true;
    public bool ChatEnabled { get; set; } = true;

    /// <summary>
    /// PES 2011 CMD_SET_QUICKMATCHOPTION values, comma-separated in order: opp_lang,
    /// oc_is_rating_rank, oc_is_rating_free, ml_is_division. Empty means the defaults.
    /// </summary>
    [MaxLength(64)]
    public string QuickMatchOptions { get; set; } = "";

    /// <summary>
    /// PES 2011 game plan (CMD_SET_GAMEPLAN): the client's formation/tactics block as the hex
    /// string it sent, kept verbatim. Empty means none saved.
    /// </summary>
    [MaxLength(4096)]
    public string GamePlan { get; set; } = "";

    /// <summary>
    /// Competition the player entered with CMD_JOIN_COMPLG, 0 for none. One at a time: the
    /// client knows ERR_COMPE_JOIN_OTHERCOMPE for a second entry.
    /// </summary>
    public int CompetitionId { get; set; }

    /// <summary>
    /// The team the player picked for <see cref="CompetitionId"/> (CMD_SET_COMPLG_TEAM): team id
    /// (0 = none picked), its name and the client's <c>member</c> list as sent, played back as is.
    /// </summary>
    public int CompetitionTeamId { get; set; }

    [MaxLength(150)]
    public string CompetitionTeamName { get; set; } = "";

    [MaxLength(16384)]
    public string CompetitionTeam { get; set; } = "";

    public DateTime? LastPlayedAt { get; set; }

    /// <summary>Completed matches since manner last moved; ten clean ones win a step back.</summary>
    public int MannerProgress { get; set; }

    // Ranked results of the running week and month (season). The week is archived to
    // WeeklyRankings and the season decides promotion and relegation when they roll over.
    public int WeeklyPoints { get; set; }
    public int WeeklyMatches { get; set; }
    public int WeeklyWins { get; set; }
    public int WeeklyDraws { get; set; }
    public int WeeklyLosses { get; set; }

    /// <summary>Rated wins in a row this week, and the best such run (the weekly streak column).</summary>
    public int WeeklyStreak { get; set; }
    public int WeeklyBestStreak { get; set; }

    public int SeasonPoints { get; set; }
    public int SeasonMatches { get; set; }

    /// <summary>
    /// Division change from the last season end not yet shown to the player, as
    /// "olddiv,oldrank,newdiv,newrank,unixdate". Empty when there is nothing to show.
    /// </summary>
    [MaxLength(64)]
    public string PendingDivisionUpdate { get; set; } = "";

    public PlayerStats Stats { get; set; } = new();
}

public sealed class PlayerStats
{
    public int Id { get; set; }
    public int PlayerId { get; set; }

    public int MatchCount { get; set; }
    public int WinCount { get; set; }
    public int LoseCount { get; set; }
    public int DrawCount { get; set; }
    public int ContinuousWins { get; set; }
    public int MaxContinuousWins { get; set; }
    public int DisconnectCount { get; set; }
    public int DisconnectWins { get; set; }
    public int DisconnectLosses { get; set; }
    public int Goals { get; set; }
    public int GoalsAgainst { get; set; }
    public int TotalCombination { get; set; }
    public int MaxCombination { get; set; }
}

public sealed class MatchRecord
{
    public long Id { get; set; }
    public int HomePid { get; set; }
    public int AwayPid { get; set; }
    public int HomeScore { get; set; }
    public int AwayScore { get; set; }
    public int HomeTeamId { get; set; }
    public int AwayTeamId { get; set; }

    /// <summary>The competition (config id) the match was played for, 0 for any other.</summary>
    public int CompeId { get; set; }

    public bool Disconnected { get; set; }
    public int? DisconnectedPid { get; set; }

    /// <summary>Ranking match (rating and points moved) rather than a friendly.</summary>
    public bool Ranked { get; set; }

    /// <summary>Room options and the CMD_SET_GAMEENV record, in the client's key=value text.</summary>
    public string Settings { get; set; } = "";

    /// <summary>
    /// Created when the match starts (its Id is the client's match_id) and completed when it
    /// ends. Only completed rows are history.
    /// </summary>
    public bool Completed { get; set; } = true;

    /// <summary>Goals per period, "1st,2nd,ex1st,ex2nd,pk".</summary>
    [MaxLength(32)]
    public string HomePeriods { get; set; } = "";

    [MaxLength(32)]
    public string AwayPeriods { get; set; } = "";

    public bool ExtraTime { get; set; }
    public bool Penalties { get; set; }

    /// <summary>Rating and points moved (ranked, not voided by the anti-farming rules).</summary>
    public bool Rated { get; set; }

    /// <summary>Goals and cards as reported by the match host, one per line (see MatchEvents).</summary>
    public string Events { get; set; } = "";

    /// <summary>
    /// Minutes per footballer from each player's CMD_SET_GAMEPLAYER_PLAYTIME,
    /// "pid|gameplayer_id|position|minutes" per line.
    /// </summary>
    public string PlayTimes { get; set; } = "";

    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The community the match was played for (a COMMU room); 0 for every other match.</summary>
    public int CommuId { get; set; }
}

public enum FriendState
{
    // Names and order are pes2011.exe enum 0x18.
    Friend = 0,
    SentReq = 1,
    RecievedReq = 2,
}

/// <summary>One side of a friendship or friend request; each pair has a row per player.</summary>
public sealed class Friendship
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public int OtherPid { get; set; }
    public FriendState State { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class BlacklistEntry
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public int TargetPid { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A short mail between players. Sender and recipient each delete their own copy; the row
/// goes once both have.
/// </summary>
public sealed class ShortMail
{
    public int Id { get; set; }
    public int FromPid { get; set; }
    public int ToPid { get; set; }

    [MaxLength(192)]
    public string Subject { get; set; } = "";

    [MaxLength(1536)]
    public string Body { get; set; } = "";

    public bool Read { get; set; }
    public bool DeletedBySender { get; set; }
    public bool DeletedByRecipient { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A message for one player, served by CMD_GET_PRIVATEINFO.</summary>
public sealed class PlayerNotice
{
    public int Id { get; set; }
    public int Pid { get; set; }

    [MaxLength(64)]
    public string Title { get; set; } = "";

    [MaxLength(512)]
    public string Body { get; set; } = "";

    public bool Important { get; set; }

    /// <summary>Already shown to the player; kept (soft-deleted) rather than removed.</summary>
    public bool Delivered { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A finished week's ranking line, kept for the weekly ranking screens.</summary>
public sealed class WeeklyRankingEntry
{
    public long Id { get; set; }

    /// <summary>Monday 00:00 UTC the week started, in unix seconds.</summary>
    public long WeekStart { get; set; }

    public int Pid { get; set; }

    [MaxLength(15)]
    public string Name { get; set; } = "";

    public int Rank { get; set; }
    public int Points { get; set; }
    public int Matches { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int Streak { get; set; }
    public int Rating { get; set; }

    public Division Division { get; set; }
}

/// <summary>
/// A player's avatar picture as CMD_SET_AVATAR uploaded it (PES 2012+). Its own table so the
/// 12 KB picture is not loaded with every player query.
/// </summary>
public sealed class PlayerAvatar
{
    public int Pid { get; set; }

    [MaxLength(12288)]
    public byte[] Data { get; set; } = [];

    /// <summary>The client's own checksum, kept as sent; CMD_GET_AVATAR hands it back.</summary>
    [MaxLength(32)]
    public string Md5 { get; set; } = "";
}

/// <summary>A community a player created (PES 2011 and later). The owner is also a <see cref="CommunityMember"/>.</summary>
public sealed class Community
{
    public int Id { get; set; }

    public int OwnerPid { get; set; }

    [MaxLength(77)]
    public string Name { get; set; } = "";

    [MaxLength(246)]
    public string Description { get; set; } = "";

    /// <summary>Plain text, as the client sends it; only the owner is ever shown it.</summary>
    [MaxLength(30)]
    public string Password { get; set; } = "";

    public bool InviteOnly { get; set; }

    /// <summary>Seven Y/N flags, Monday first as the client lists them.</summary>
    [MaxLength(7)]
    public string Week { get; set; } = "YYYYYYY";

    /// <summary>The hour range members usually play in; 0 and 0 means never set.</summary>
    public int StartTime { get; set; }

    public int EndTime { get; set; }

    /// <summary>Unix seconds.</summary>
    public long CreatedAt { get; set; }
}

public sealed class CommunityMember
{
    public int CommuId { get; set; }

    public int Pid { get; set; }

    /// <summary>Unix seconds.</summary>
    public long JoinedAt { get; set; }
}

/// <summary>A league a community member opened (PES 2011 and later). Teams and matches are not stored yet.</summary>
public sealed class CommunityLeague
{
    public int Id { get; set; }

    public int CommuId { get; set; }

    /// <summary>The member who opened it.</summary>
    public int ChairmanPid { get; set; }

    [MaxLength(40)]
    public string Name { get; set; } = "";

    /// <summary>Enum TIME_5, TIME_10 or TIME_15 as the client sends it.</summary>
    [MaxLength(10)]
    public string MatchTime { get; set; } = "TIME_5";

    public bool Injury { get; set; }

    public int BallType { get; set; }

    public bool HomeAndAway { get; set; }

    [MaxLength(255)]
    public string Comment { get; set; } = "";

    /// <summary>Unix seconds.</summary>
    public long CreatedAt { get; set; }

    public bool Finished { get; set; }

    /// <summary>Unix seconds; 0 while open.</summary>
    public long FinishedAt { get; set; }
}

/// <summary>A member signed up to a <see cref="CommunityLeague"/>.</summary>
public sealed class CommunityLeagueMember
{
    public int CompeId { get; set; }

    public int Pid { get; set; }

    /// <summary>The <see cref="CommunityLeagueCell"/> the member entered; 0 while they have none.</summary>
    public int CellId { get; set; }
}

/// <summary>A team slot of a league: one or more members play it together.</summary>
public sealed class CommunityLeagueCell
{
    public int Id { get; set; }

    public int CompeId { get; set; }

    /// <summary>The club team the members picked (CMD_SET_COMMU_TEAM); 0 until they do.</summary>
    public int TeamId { get; set; }

    /// <summary>
    /// The <c>member</c> list of CMD_SET_COMMU_TEAM exactly as the client sent it (key=value text),
    /// played back by CMD_GET_COMMU_TEAM so the footballer details survive untouched.
    /// </summary>
    public string TeamMembers { get; set; } = "";
}

/// <summary>A fixture of a league: two cells play each other (twice, home and away, when the league says so).</summary>
public sealed class CommunityLeagueMatch
{
    public int Id { get; set; }

    public int CompeId { get; set; }

    public int HomeCellId { get; set; }

    public int AwayCellId { get; set; }

    public bool Finished { get; set; }

    /// <summary>Goals per period of the league's home and away cell, "1st,2nd,ex1st,ex2nd,pk".</summary>
    [MaxLength(32)]
    public string HomePeriods { get; set; } = "";

    [MaxLength(32)]
    public string AwayPeriods { get; set; } = "";

    public int HomeGoals { get; set; }

    public int AwayGoals { get; set; }

    /// <summary>The <see cref="MatchRecord"/> that played it, once it has been.</summary>
    public long MatchRecordId { get; set; }

    /// <summary>Unix seconds; 0 until played.</summary>
    public long PlayedAt { get; set; }
}

/// <summary>
/// A competition a player has entered (CMD_JOIN_COMPLG), kept after the entry ends: the competition
/// menu counts these (PLAYERINFO <c>compe_list</c>: join_num per kind).
/// </summary>
public sealed class CompetitionEntry
{
    public int Id { get; set; }

    public int Pid { get; set; }

    public int CompeId { get; set; }

    /// <summary>The competition's compe_kind and name when it was entered.</summary>
    [MaxLength(48)]
    public string Kind { get; set; } = "";

    [MaxLength(96)]
    public string Name { get; set; } = "";

    /// <summary>Unix seconds.</summary>
    public long JoinedAt { get; set; }
}

/// <summary>Small server bookkeeping values (last processed week and season).</summary>
public sealed class ServerSetting
{
    [MaxLength(64)]
    public string Key { get; set; } = "";

    [MaxLength(256)]
    public string Value { get; set; } = "";
}

/// <summary>News item served by CMD_GET_INFORMATIONLIST. Editable without a redeploy.</summary>
public sealed class InformationItem
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string Subject { get; set; } = "";

    [MaxLength(512)]
    public string Body { get; set; } = "";

    public bool Important { get; set; }
    public bool Present { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}

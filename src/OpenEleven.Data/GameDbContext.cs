// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.EntityFrameworkCore;
using OpenEleven.Data.Entities;

namespace OpenEleven.Data;

public sealed class GameDbContext(DbContextOptions<GameDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<PlayerStats> PlayerStats => Set<PlayerStats>();
    public DbSet<MatchRecord> Matches => Set<MatchRecord>();
    public DbSet<InformationItem> Information => Set<InformationItem>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<BlacklistEntry> Blacklist => Set<BlacklistEntry>();
    public DbSet<PlayerNotice> Notices => Set<PlayerNotice>();
    public DbSet<ShortMail> ShortMails => Set<ShortMail>();
    public DbSet<WeeklyRankingEntry> WeeklyRankings => Set<WeeklyRankingEntry>();
    public DbSet<ServerSetting> Settings => Set<ServerSetting>();
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<CommunityMember> CommunityMembers => Set<CommunityMember>();
    public DbSet<CommunityLeague> CommunityLeagues => Set<CommunityLeague>();
    public DbSet<CommunityLeagueMember> CommunityLeagueMembers => Set<CommunityLeagueMember>();
    public DbSet<CommunityLeagueCell> CommunityLeagueCells => Set<CommunityLeagueCell>();
    public DbSet<CommunityLeagueMatch> CommunityLeagueMatches => Set<CommunityLeagueMatch>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.GameId).IsUnique();

            // Shared product serials are valid. This is an administrative index only.
            e.HasIndex(x => x.RegCode);

            e.HasMany(x => x.Players)
                .WithOne(x => x.Account!)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Player>(e =>
        {
            e.HasKey(x => x.Pid);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.NormalizedName).IsUnique();
            e.HasIndex(x => x.AccountId).IsUnique();
            // DivisionIndex, not Division: databases before this held the name in a text column of that name.
            e.Property(x => x.Division).HasColumnName("DivisionIndex");
            e.HasOne(x => x.Stats)
                .WithOne()
                .HasForeignKey<PlayerStats>(x => x.PlayerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PlayerStats>(e => e.HasKey(x => x.Id));

        b.Entity<MatchRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.HomePid);
            e.HasIndex(x => x.AwayPid);
            e.HasIndex(x => x.PlayedAt);
        });

        b.Entity<InformationItem>(e => e.HasKey(x => x.Id));

        b.Entity<Friendship>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Pid, x.OtherPid }).IsUnique();
        });

        b.Entity<BlacklistEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Pid, x.TargetPid }).IsUnique();
        });

        b.Entity<ShortMail>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ToPid);
            e.HasIndex(x => x.FromPid);
        });

        b.Entity<PlayerNotice>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Pid);
        });

        b.Entity<WeeklyRankingEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.WeekStart, x.Rank });
            e.Property(x => x.Division).HasColumnName("DivisionIndex");
        });

        b.Entity<ServerSetting>(e => e.HasKey(x => x.Key));

        b.Entity<Community>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerPid);
        });

        b.Entity<CommunityLeague>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CommuId);
        });

        b.Entity<CommunityLeagueMember>(e => e.HasKey(x => new { x.CompeId, x.Pid }));

        b.Entity<CommunityLeagueMatch>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CompeId);
        });

        b.Entity<CommunityLeagueCell>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.CompeId);
        });

        // No foreign keys: a deleted player's rows are skipped by the joins instead of cascading.
        b.Entity<CommunityMember>(e =>
        {
            e.HasKey(x => new { x.CommuId, x.Pid });
            e.HasIndex(x => x.Pid);
        });
    }
}

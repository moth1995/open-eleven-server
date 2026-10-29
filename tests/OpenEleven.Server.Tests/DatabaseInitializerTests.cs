// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenEleven.Data;
using OpenEleven.Data.Entities;

namespace OpenEleven.Server.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task Upgrades_legacy_sqlite_account_authentication_columns()
    {
        var databaseFile = Path.Combine(
            Path.GetTempPath(), $"OpenEleven-legacy-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={databaseFile};Pooling=False";
            await CreateLegacyDatabaseAsync(connectionString);

            var options = new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var db = new GameDbContext(options);
            var initializer = new DatabaseInitializer(
                db, NullLogger<DatabaseInitializer>.Instance);

            await initializer.InitializeAsync();

            var account = await db.Accounts.SingleAsync();
            Assert.Equal("legacy-player", account.GameId);
            Assert.Equal("", account.PasswordHash);
            Assert.Null(account.RegCode);
            Assert.True(account.FirstLogin);
            Assert.Equal("LEGACY PROFILE", (await db.Players.SingleAsync()).NormalizedName);

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA index_list('Accounts');";
            await using var reader = await command.ExecuteReaderAsync();

            var foundNonUniqueRegCodeIndex = false;
            while (await reader.ReadAsync())
            {
                if (reader.GetString(1) == "IX_Accounts_RegCode")
                    foundNonUniqueRegCodeIndex = reader.GetInt32(2) == 0;
            }

            Assert.True(foundNonUniqueRegCodeIndex);

            await reader.DisposeAsync();
            command.CommandText = "PRAGMA index_list('Players');";
            await using var playerIndexReader = await command.ExecuteReaderAsync();
            var uniqueIndexes = new HashSet<string>(StringComparer.Ordinal);
            while (await playerIndexReader.ReadAsync())
            {
                if (playerIndexReader.GetInt32(2) == 1)
                    uniqueIndexes.Add(playerIndexReader.GetString(1));
            }

            Assert.Contains("IX_Players_AccountId", uniqueIndexes);
            Assert.Contains("IX_Players_NormalizedName", uniqueIndexes);
        }
        finally
        {
            File.Delete(databaseFile);
        }
    }

    // Divisions used to be stored as names; both ladders (D5..D1 and D3C..D1) are the same five
    // steps, so a name becomes its position, in the player table and in the archived weeks.
    [Theory]
    [InlineData("D5", Division.D5)]
    [InlineData("D3C", Division.D5)]
    [InlineData("D4", Division.D4)]
    [InlineData("D3B", Division.D4)]
    [InlineData("D3", Division.D3)]
    [InlineData("D3A", Division.D3)]
    [InlineData("D2", Division.D2)]
    [InlineData("D1", Division.D1)]
    [InlineData("", Division.D5)]
    public async Task Upgrades_division_names_to_positions_and_drops_the_text_columns(string stored, Division expected)
    {
        var databaseFile = Path.Combine(Path.GetTempPath(), $"OpenEleven-division-{Guid.NewGuid():N}.db");

        try
        {
            var connectionString = $"Data Source={databaseFile};Pooling=False";
            await CreateLegacyDatabaseAsync(connectionString);
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                    UPDATE "Players" SET "Division" = '{stored}';
                    CREATE TABLE "WeeklyRankings" (
                        "Id" INTEGER NOT NULL CONSTRAINT "PK_WeeklyRankings" PRIMARY KEY AUTOINCREMENT,
                        "WeekStart" INTEGER NOT NULL, "Pid" INTEGER NOT NULL, "Name" TEXT NOT NULL,
                        "Rank" INTEGER NOT NULL, "Points" INTEGER NOT NULL, "Matches" INTEGER NOT NULL,
                        "Wins" INTEGER NOT NULL, "Draws" INTEGER NOT NULL, "Losses" INTEGER NOT NULL,
                        "Streak" INTEGER NOT NULL, "Rating" INTEGER NOT NULL, "Division" TEXT NOT NULL);
                    INSERT INTO "WeeklyRankings" VALUES (1, 100, 1, 'Legacy Profile', 1, 0, 0, 0, 0, 0, 0, 500, '{stored}');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connectionString).Options;
            await using var db = new GameDbContext(options);
            await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();

            Assert.Equal(expected, (await db.Players.SingleAsync()).Division);
            Assert.Equal(expected, (await db.WeeklyRankings.SingleAsync()).Division);

            // The old NOT NULL text column would fail this insert.
            var second = new Account { GameId = "second", PasswordHash = "" };
            second.Players.Add(new Player { Name = "Second", NormalizedName = "SECOND", Stats = new PlayerStats() });
            db.Accounts.Add(second);
            await db.SaveChangesAsync();

            // A second start finds nothing left to move.
            await new DatabaseInitializer(db, NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
            Assert.Equal(expected, (await db.Players.OrderBy(p => p.Pid).FirstAsync()).Division);
        }
        finally
        {
            File.Delete(databaseFile);
        }
    }

    [Fact]
    public async Task Removes_only_the_untouched_legacy_demo_account_and_profile()
    {
        var databaseFile = Path.Combine(
            Path.GetTempPath(), $"OpenEleven-demo-cleanup-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={databaseFile};Pooling=False")
                .Options;
            await using var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            db.Accounts.Add(LegacyDemoAccount());
            await db.SaveChangesAsync();

            var initializer = new DatabaseInitializer(
                db, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            Assert.Empty(await db.Accounts.ToListAsync());
            Assert.Empty(await db.Players.ToListAsync());
        }
        finally
        {
            File.Delete(databaseFile);
        }
    }

    [Fact]
    public async Task Preserves_a_modified_legacy_demo_profile()
    {
        var databaseFile = Path.Combine(
            Path.GetTempPath(), $"OpenEleven-demo-preserve-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<GameDbContext>()
                .UseSqlite($"Data Source={databaseFile};Pooling=False")
                .Options;
            await using var db = new GameDbContext(options);
            await db.Database.EnsureCreatedAsync();
            var account = LegacyDemoAccount();
            account.Players[0].Intro = "user changed this";
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            var initializer = new DatabaseInitializer(
                db, NullLogger<DatabaseInitializer>.Instance);
            await initializer.InitializeAsync();

            Assert.Equal("user changed this", (await db.Players.SingleAsync()).Intro);
            Assert.Equal("LOCAL PLAYER", (await db.Players.SingleAsync()).NormalizedName);
        }
        finally
        {
            File.Delete(databaseFile);
        }
    }

    private static async Task CreateLegacyDatabaseAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE "Accounts" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY AUTOINCREMENT,
                "GameId" TEXT NOT NULL,
                "Banned" INTEGER NOT NULL,
                "BanReason" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "LastLoginAt" TEXT NULL
            );
            CREATE UNIQUE INDEX "IX_Accounts_GameId" ON "Accounts" ("GameId");
            CREATE INDEX "IX_Accounts_RegCode" ON "Accounts" ("RegCode");
            CREATE TABLE "Players" (
                "Pid" INTEGER NOT NULL CONSTRAINT "PK_Players" PRIMARY KEY AUTOINCREMENT,
                "AccountId" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Division" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Lang" TEXT NOT NULL,
                "Intro" TEXT NOT NULL,
                "Rating" INTEGER NOT NULL,
                "Point" INTEGER NOT NULL,
                "Rank" INTEGER NOT NULL,
                "Manner" INTEGER NOT NULL,
                "Country" INTEGER NOT NULL,
                "Area" INTEGER NOT NULL,
                "BirthMonth" INTEGER NOT NULL,
                "BirthDay" INTEGER NOT NULL,
                "FavoriteTeam" INTEGER NOT NULL,
                "FavoritePlayer" INTEGER NOT NULL,
                "SelfReportLevel" TEXT NOT NULL,
                "PositionWant" TEXT NOT NULL,
                "DesiredPositionMask" INTEGER NOT NULL,
                "AutoMatchWant" INTEGER NOT NULL,
                "BeginnerMark" INTEGER NOT NULL,
                "ChatEnabled" INTEGER NOT NULL,
                CONSTRAINT "FK_Players_Accounts_AccountId" FOREIGN KEY ("AccountId")
                    REFERENCES "Accounts" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_Players_AccountId" ON "Players" ("AccountId");
            CREATE UNIQUE INDEX "IX_Players_Name" ON "Players" ("Name");
            CREATE TABLE "PlayerStats" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_PlayerStats" PRIMARY KEY AUTOINCREMENT,
                "PlayerId" INTEGER NOT NULL,
                "MatchCount" INTEGER NOT NULL,
                "WinCount" INTEGER NOT NULL,
                "LoseCount" INTEGER NOT NULL,
                "DrawCount" INTEGER NOT NULL,
                "ContinuousWins" INTEGER NOT NULL,
                "MaxContinuousWins" INTEGER NOT NULL,
                "DisconnectCount" INTEGER NOT NULL,
                "DisconnectWins" INTEGER NOT NULL,
                "DisconnectLosses" INTEGER NOT NULL,
                "Goals" INTEGER NOT NULL,
                "GoalsAgainst" INTEGER NOT NULL,
                "TotalCombination" INTEGER NOT NULL,
                "MaxCombination" INTEGER NOT NULL,
                CONSTRAINT "FK_PlayerStats_Players_PlayerId" FOREIGN KEY ("PlayerId")
                    REFERENCES "Players" ("Pid") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX "IX_PlayerStats_PlayerId" ON "PlayerStats" ("PlayerId");
            INSERT INTO "Accounts" (
                "GameId", "Banned", "BanReason", "CreatedAt", "LastLoginAt")
            VALUES ('legacy-player', 0, NULL, CURRENT_TIMESTAMP, NULL);
            INSERT INTO "Players" (
                "AccountId", "Name", "Division", "Kind", "Lang", "Intro",
                "Rating", "Point", "Rank", "Manner", "Country", "Area",
                "BirthMonth", "BirthDay", "FavoriteTeam", "FavoritePlayer",
                "SelfReportLevel", "PositionWant", "DesiredPositionMask",
                "AutoMatchWant", "BeginnerMark", "ChatEnabled")
            VALUES (
                1, 'Legacy Profile', 'D3C', 'NORMAL', 'EN', '',
                500, 0, 0, 3, 0, 0, 0, 0, 0, 0,
                'PRO', 'CF', 0, 1, 1, 1);
            INSERT INTO "PlayerStats" (
                "PlayerId", "MatchCount", "WinCount", "LoseCount", "DrawCount",
                "ContinuousWins", "MaxContinuousWins", "DisconnectCount",
                "DisconnectWins", "DisconnectLosses", "Goals", "GoalsAgainst",
                "TotalCombination", "MaxCombination")
            VALUES (1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static Account LegacyDemoAccount()
        => new()
        {
            GameId = "local",
            PasswordHash = "",
            RegCode = null,
            FirstLogin = true,
            Players =
            [
                new Player
                {
                    Name = "Local Player",
                    NormalizedName = "LOCAL PLAYER",
                    Division = Division.D5,
                    Kind = "NORMAL",
                    Lang = "EN",
                    Intro = "PLAYERINFO FIELD TEST",
                    Rating = 742,
                    Point = 12345,
                    Rank = 321,
                    Manner = 3,
                    Country = 50,
                    BirthMonth = 9,
                    BirthDay = 12,
                    FavoriteTeam = 5,
                    FavoritePlayer = 4618,
                    SelfReportLevel = "PRO",
                    PositionWant = "CF",
                    DesiredPositionMask = DatabaseInitializer.LegacyDesiredPositionMask,
                    AutoMatchWant = true,
                    BeginnerMark = true,
                    ChatEnabled = true,
                    Stats = new PlayerStats
                    {
                        MatchCount = 37,
                        WinCount = 21,
                        LoseCount = 9,
                        DrawCount = 7,
                        ContinuousWins = 5,
                        MaxContinuousWins = 8,
                        DisconnectCount = 2,
                        DisconnectWins = 1,
                        DisconnectLosses = 1,
                        Goals = 84,
                        GoalsAgainst = 42,
                        TotalCombination = 18,
                        MaxCombination = 4,
                    },
                },
            ],
        };
}

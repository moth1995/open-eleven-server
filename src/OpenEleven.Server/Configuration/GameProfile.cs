// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.Configuration;

/// <summary>
/// The game title a server process serves. One process serves exactly one title, so a
/// configured value must always be a single flag; <see cref="All"/> exists only as the
/// default filter on <c>CommandAttribute.Profiles</c> for commands shared by every title.
/// </summary>
[Flags]
public enum GameProfile
{
    Pes2010Pc = 1 << 0,
    Pes2011Pc = 1 << 1,
    Pes2012Pc = 1 << 2,
    Pes2013Pc = 1 << 3,

    All = Pes2010Pc | Pes2011Pc | Pes2012Pc | Pes2013Pc,
}

/// <summary>
/// Profile families. PES 2010 is the odd one out; PES 2011 and everything after it share the
/// session protocol, so code branches on "is it 2010" first and lets every other title fall
/// into the modern path.
/// </summary>
public static class GameProfiles
{
    public const GameProfile Modern = GameProfile.Pes2011Pc | GameProfile.Pes2012Pc | GameProfile.Pes2013Pc;

    /// <summary>Titles whose reply tables carry the keys PES 2011 lacks, and the commands only they send.</summary>
    public const GameProfile Since2012 = GameProfile.Pes2012Pc | GameProfile.Pes2013Pc;

    public static bool IsModern(this GameProfile profile) => profile != GameProfile.Pes2010Pc;

    /// <summary>Titles whose reply tables carry the keys PES 2011 lacks (FD_rank, is_restricted, ...).</summary>
    public static bool IsSince2012(this GameProfile profile) => profile is GameProfile.Pes2012Pc or GameProfile.Pes2013Pc;
}

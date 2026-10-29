// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.State;

/// <summary>
/// The player details a list entry carries inline (PES 2012 puts them in every room's gamer
/// entries). A snapshot from when the profile was selected: rating and division refresh at the
/// next login, not after each match.
/// </summary>
public sealed record PlayerCard(string Division, int Rating, string Lang, string SelfReportLevel, string PositionWant)
{
    public static readonly PlayerCard Default = new("D5", 1000, "EN", "PRO", "CF");
}

// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Server.State;

/// <summary>
/// A league fixture one player has put up in a room (CMD_APPLY_COMMU_MATCH) while the other cell
/// decides. Kept in memory: an offer only means something while the room it names is open.
/// </summary>
public sealed class CommunityMatchOffer(int matchId, int compeId, int applicantPid, int roomId)
{
    public int MatchId { get; } = matchId;
    public int CompeId { get; } = compeId;
    public int ApplicantPid { get; } = applicantPid;
    public int RoomId { get; } = roomId;

    /// <summary>The players of the other cell who said yes.</summary>
    public HashSet<int> Accepted { get; } = [];

    public bool Canceled { get; set; }
}

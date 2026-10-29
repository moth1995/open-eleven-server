// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

namespace OpenEleven.Data.Entities;

/// <summary>
/// A rank-ladder step, lowest first; the database stores the number. Every title has the same
/// five steps in the same order, only the names differ: PES 2011 and later call them D5..D1
/// (these members), PES 2010 calls them D3C, D3B, D3A, D2, D1.
/// </summary>
public enum Division
{
    D5 = 0,
    D4 = 1,
    D3 = 2,
    D2 = 3,
    D1 = 4,
}

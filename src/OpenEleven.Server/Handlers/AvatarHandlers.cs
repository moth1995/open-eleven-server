// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using System.Security.Cryptography;
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
/// Player pictures (PES 2012 and later). Key names and error lists come from the strings
/// pes2012.exe carries next to each command (.rdata 0x01432e78..0x01468af8); the code that parses
/// the replies is not recoverable statically, so every handler answers with the least that lets
/// the screen through.
/// </summary>
public sealed class AvatarHandlers(GameDbContext db, ILogger<AvatarHandlers> log)
{
    /// <summary>The client sends a 64x48 RGBA-ish block: 12288 bytes, hex encoded.</summary>
    private const int MaxAvatarBytes = 12288;

    /// <summary>
    /// The player's avatar picture: <c>avatar_data_size</c> bytes as hex plus their MD5, verified
    /// with the client's own errors and saved for <see cref="GetAvatar"/>.
    /// </summary>
    [Command("CMD_SET_AVATAR", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public async ValueTask<KvMessage[]> SetAvatar(CommandContext ctx)
    {
        var size = ctx.Request.GetInt32("avatar_data_size", 0);
        var hex = ctx.Request.GetString("avatar_data") ?? "";
        if (size is <= 0 or > MaxAvatarBytes)
            return [ctx.Fail("ERR_DATASIZEOVER")];

        if (hex.Length != size * 2 || !hex.All(Uri.IsHexDigit))
            return [ctx.Fail("ERR_MD5")];

        var data = Convert.FromHexString(hex);
        var sentMd5 = ctx.Request.GetString("avatar_md5") ?? "";
        var md5 = Convert.ToHexString(MD5.HashData(data));
        if (!string.Equals(md5, sentMd5, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("CMD_SET_AVATAR from {Session}: md5 {Md5} does not match the data", ctx.Session, md5);
            return [ctx.Fail("ERR_MD5")];
        }

        var avatar = await db.Avatars.FindAsync([ctx.Session.Pid], ctx.CancellationToken);
        if (avatar is null)
            db.Avatars.Add(avatar = new PlayerAvatar { Pid = ctx.Session.Pid });
        avatar.Data = data;
        avatar.Md5 = sentMd5;
        await db.SaveChangesAsync(ctx.CancellationToken);
        return [ctx.Ok()];
    }

    /// <summary>
    /// Another player's avatar (request: pid). FUN_006c16a0 reads avatar_data_size and stops at 0,
    /// which is the "no picture" answer; otherwise it checks avatar_data against avatar_md5. The
    /// client uploads uppercase hex, which is what Convert.ToHexString gives back.
    /// </summary>
    [Command("CMD_GET_AVATAR", Roles = ServiceRole.Menu, Profiles = GameProfiles.Since2012)]
    public async ValueTask<KvMessage[]> GetAvatar(CommandContext ctx)
    {
        var pid = ctx.Request.GetInt32("pid", ctx.Session.Pid);
        var avatar = await db.Avatars.AsNoTracking().FirstOrDefaultAsync(a => a.Pid == pid, ctx.CancellationToken);
        if (avatar is null)
            return [ctx.Ok().Set("avatar_data_size", 0)];

        return [ctx.Ok()
            .Set("avatar_data_size", avatar.Data.Length)
            .Set("avatar_md5", avatar.Md5)
            .Set("avatar_data", Convert.ToHexString(avatar.Data))];
    }
}

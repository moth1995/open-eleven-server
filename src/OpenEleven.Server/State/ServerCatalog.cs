// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Options;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;

namespace OpenEleven.Server.State;

/// <summary>
/// Builds the CMD_GET_SVRLIST payload from configuration. Ports and the advertised
/// address come from config rather than constants, which is what makes the one-port-per-
/// service split possible without touching handler code.
/// </summary>
public sealed class ServerCatalog
{
    private readonly IOptionsMonitor<ServerOptions> _options;
    private readonly Hub _hub;

    public ServerCatalog(IOptionsMonitor<ServerOptions> options, Hub hub)
    {
        _options = options;
        _hub = hub;
        AdvertiseIp = AdvertiseIpResolver.Resolve(options.CurrentValue.AdvertiseIp);
        ServerAddress = AdvertiseIpResolver.ToIPv4(AdvertiseIp);
    }

    /// <summary>Resolved once at startup so every response agrees on one address.</summary>
    public string AdvertiseIp { get; }

    /// <summary>
    /// <see cref="AdvertiseIp"/> as an IPv4 address, for <c>svraddr</c>: the server list carries
    /// IPs (as Konami's did), while the URLs keep the host name the certificate is issued for.
    /// </summary>
    public string ServerAddress { get; }

    public IReadOnlyList<ServiceEndpointOptions> Endpoints
        => _options.CurrentValue.Services.Where(s => s.Enabled).ToArray();

    /// <summary>
    /// PES 2011 entry shape (carried as <c>server_list</c>: the parser builds its field paths as
    /// <c>server_list[%d].%s</c>, <c>pes2011.exe</c> 0x013ba1a4), exactly the four fields it
    /// requires: <c>svrtype</c>, <c>svraddr</c>, <c>svrport</c>, <c>userCount</c>. The 2010-only
    /// <c>svrname</c>/<c>svrgid</c>/<c>max_player_num</c>/<c>player_num</c> are absent because
    /// that parser never reads them and an unknown key would only risk a format error.
    /// </summary>
    public IReadOnlyList<KvMessage> BuildPes2011Entries()
        => _options.CurrentValue.Services
            .Where(s => s is { Enabled: true, Advertise: true })
            .OrderBy(s => s.Gid)
            .Select(s => (Endpoint: s, SvrType: s.Role.ToPes2011SvrType()))
            .Where(x => x.SvrType is not null)
            .Select(x => new KvMessage()
                .Set("svrtype", x.SvrType!)
                .Set("svraddr", ServerAddress)
                .Set("svrport", x.Endpoint.Port)
                .Set("userCount", _hub.PlayerCount(x.Endpoint.Role)))
            .ToArray();

    /// <summary>PES 2010-shaped entries minus <c>svrname</c>, which FUN_005104f0 never reads.</summary>
    public IReadOnlyList<KvMessage> BuildLegacyEntries()
        => _options.CurrentValue.Services
            .Where(s => s is { Enabled: true, Advertise: true })
            .Where(s => s.Role.ToPes2011SvrType() is not null)
            .OrderBy(s => s.Gid)
            .Select(s => new KvMessage()
                .Set("svrtype", s.Role.ToSvrType())
                .Set("svrport", s.Port)
                .Set("svraddr", ServerAddress)
                .Set("max_player_num", s.MaxPlayers)
                .Set("player_num", _hub.PlayerCount(s.Role))
                .Set("svrgid", s.Gid))
            .ToArray();

    /// <summary>PES 2010 entry shape, carried as <c>svrlist</c>.</summary>
    public IReadOnlyList<KvMessage> BuildPes2010Entries()
        => _options.CurrentValue.Services
            .Where(s => s is { Enabled: true, Advertise: true })
            .OrderBy(s => s.Gid)
            .Select(s => new KvMessage()
                .Set("svrtype", s.Role.ToSvrType())
                .Set("svrname", s.Name)
                .Set("svrport", s.Port)
                .Set("svraddr", ServerAddress)
                .Set("max_player_num", s.MaxPlayers)
                .Set("player_num", _hub.PlayerCount(s.Role))
                .Set("svrgid", s.Gid))
            .ToArray();
}

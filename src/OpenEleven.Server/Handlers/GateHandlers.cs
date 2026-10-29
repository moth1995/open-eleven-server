// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;
using OpenEleven.Server.Web;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// Discovery stage: everything the client asks for before it has an account.
/// Served on the gate port, which is the one port the client knows without being told.
/// </summary>
public sealed class GateHandlers(
    IOptionsMonitor<ServerOptions> options,
    ServerCatalog catalog,
    WebAssets assets,
    ICatalogRepository repository,
    ILogger<GateHandlers> log)
{
    private const ServiceRole AnyServiceRole = ServiceRole.All;

    // PES 2011 and later answer CMD_GET_SVRLIST and CMD_DISCONNECT with a different payload
    // shape and have their own handlers (below); these are PES 2010's svrlist/svrgid.
    // The reference server echoed the whole server list back on disconnect; the client
    // tolerates it and it lets a reconnect skip a round trip.
    [Command("CMD_GET_SVRLIST", Roles = ServiceRole.Gate, Profiles = GameProfile.Pes2010Pc, RequiredState = SessionState.Connected)]
    [Command("CMD_DISCONNECT", Roles = AnyServiceRole, Profiles = GameProfile.Pes2010Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetServerListPes2010(CommandContext ctx)
        => Reply.Of(ctx.Ok().SetList("server_num", "svrlist", catalog.BuildPes2010Entries()));

    /// <summary>
    /// The server list the client uses to find ACCOUNT/MENU. The response parser
    /// (<c>FUN_00ffd160</c>) addresses entries as <c>server_list[i].svrtype|svraddr|svrport|userCount</c>
    /// (format literal <c>server_list[%d].%s</c> at 0x013ba1a4) and raises
    /// <c>ERR_CLIENT_OPTFORMAT</c> for any entry missing one of those four fields, so the
    /// payload carries exactly those and nothing else.
    /// </summary>
    [Command("CMD_GET_SVRLIST", Roles = ServiceRole.Gate, Profiles = GameProfiles.Modern, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetServerList(CommandContext ctx)
    {
        var entries = catalog.BuildPes2011Entries();
        if (entries.Count == 0)
        {
            // The client's own canned failure shape: 0x013ba3a0 holds
            // result="%s",server_num=0,server_list=[] and SvrTypeErr is the only gate-layer
            // error literal in the binary.
            log.LogError(
                "PES 2011 CMD_GET_SVRLIST has nothing to advertise: no enabled endpoint maps to " +
                "an svrtype the client's parser recognises");

            return Reply.Of(new KvMessage()
                .Set("result", "SvrTypeErr")
                .Set("msg", ctx.Msg)
                .Set("rqid", ctx.Rqid)
                .SetList("server_num", "server_list", Array.Empty<KvMessage>()));
        }

        // The gate state machine (pes2011.exe FUN_0061e620, state 0x11) re-parses this same
        // reply with FUN_005104f0, which reads the PES 2010 shape (svrlist + svrport/max_player_num/
        // player_num/svrgid). A reply without it leaves the gate's +0x68 flag at 0, and state
        // 0x12 then ends the session through the error path ("An error has occurred"). So carry
        // both lists; each parser ignores the other's key.
        var legacy = catalog.BuildLegacyEntries();
        return Reply.Of(ctx.Ok()
            .SetList("server_num", "server_list", entries)
            .Set("svrlist", legacy));
    }

    /// <summary>The server list again, as the reference server echoed it on disconnect.</summary>
    [Command("CMD_DISCONNECT", Roles = ServiceRole.All, Profiles = GameProfiles.Modern, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> Disconnect(CommandContext ctx)
        => Reply.Of(ctx.Ok().SetList("server_num", "server_list", catalog.BuildPes2011Entries()));

    /// <summary>
    /// The server-wide knobs the client reads once (they sit between the version/date keys and
    /// <c>url_num</c> in the client's key table). A 0 level or warning threshold switches the
    /// warning off. The community limits are only read once communities exist, so they are
    /// placeholders.
    /// </summary>
    [Command("CMD_GET_SERVER_ENV", Roles = ServiceRole.All, Profiles = GameProfiles.Since2012, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetServerEnv(CommandContext ctx)
        => Reply.Of(ctx.Ok()
            .Set("normalend_all_rate_level", 0)
            .Set("normalend_10_rate_level", 0)
            .Set("manner_warning_at_login", 0)
            .Set("manner_warning_at_result", 0)
            .Set("commu_member_limited_max_num", 50)
            .Set("commu_topic_limited_max_num", 100));

    [Command("CMD_GET_SVRVERSION", Roles = AnyServiceRole, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetVersion(CommandContext ctx)
    {
        var protocol = options.CurrentValue.Protocol;
        return Reply.Of(ctx.Ok()
            .Set("version", protocol.ServerVersion)
            .Set("patch_version", protocol.PatchVersion)
            .Set("dlc_version", protocol.DlcVersion)
            .Set("eula_version", protocol.EulaVersion));
    }

    [Command("CMD_GET_SVRTIME", Roles = AnyServiceRole, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetTime(CommandContext ctx)
        => Reply.Of(ctx.Ok().Set("date", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    [Command("CMD_GET_EULA", Roles = ServiceRole.Gate | ServiceRole.Account, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetEula(CommandContext ctx)
    {
        ctx.Session.EulaAccepted = true;
        if (ctx.Session.State < SessionState.EulaAccepted)
            ctx.Session.State = SessionState.EulaAccepted;

        return Reply.Of(ctx.Ok().Set("eula", "1"));
    }

    [Command("CMD_GET_URLLIST", Roles = ServiceRole.Gate | ServiceRole.Account, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetUrlList(CommandContext ctx)
    {
        // The client fetches these over HTTP right after this reply and does not come
        // back until it succeeds, so the URLs must name the port actually being served.
        // Port 80 is left implicit to keep the payload identical to the reference server.
        // https on the title's own port, or plain http on 80 for local development: the client
        // turns TLS on for every port except 80 (pes2011.exe 0x00c4965b).
        var tls = options.CurrentValue.Https.Enabled;
        var scheme = tls ? "https" : "http";
        var port = tls ? options.CurrentValue.Listen.Https : options.CurrentValue.Listen.Http;
        var host = port == (tls ? 443 : 80)
            ? $"{scheme}://{catalog.AdvertiseIp}"
            : $"{scheme}://{catalog.AdvertiseIp}:{port}";

        var entries = new List<KvMessage>
        {
            UrlEntry("PC_SPEC", $"{host}/pcspec.bin", assets.SizeOf("/pcspec.bin")),
            UrlEntry("KONAMIID", $"{host}/register", assets.SizeOf("/register")),
            UrlEntry("GAMEID_AUTH", $"{host}/gameid_auth", assets.SizeOf("/gameid_auth")),
            UrlEntry("EULA_PES", $"{host}/eula_pes.txt", assets.SizeOf("/eula_pes.txt")),
        };

        if (options.CurrentValue.GameProfile.IsModern())
        {
            // The terms text is served by /eula_pes.txt (GET_HTTP), and the ranking screen
            // posts to RANK, or RANK_WEEK for an archived week. FUN_005f0a40 dereferences the
            // RANK_WEEK lookup unchecked, so it has to exist.
            // The request carries the client's language (lang="EN"); the texts behind these urls
            // are served in it.
            var lang = Uri.EscapeDataString(ctx.Request.GetString("lang") ?? ctx.Session.Language ?? "EN");
            entries.RemoveAll(e => e.GetString("type") == "EULA_PES");
            entries.Add(UrlEntry("EULA_PES", $"{host}/eula_pes.txt?lang={lang}",
                System.Text.Encoding.UTF8.GetByteCount(Web.GameEndpoints.EulaText(options.CurrentValue, lang))));
            entries.Add(UrlEntry("RANK_WEEK", $"{host}/ranking", 0));

            // The head-to-head screens of a community. The client puts commu_id=.. straight after
            // the url, so each ends in "?".
            entries.Add(UrlEntry("COMMU_MEMBERS_VS", $"{host}/commu_members_vs?", 0));
            entries.Add(UrlEntry("COMMU_TEAM_VS", $"{host}/commu_team_vs?", 0));

            foreach (var url in options.CurrentValue.Urls)
            {
                entries.RemoveAll(e => e.GetString("type") == url.Type);
                entries.Add(url switch
                {
                    // DOWNLOAD names the update manifest, not the package (see DownloadFiles.Manifest).
                    { Type: "DOWNLOAD", File.Length: > 0 } => UrlEntry(url.Type, $"{host}/dlc/manifest/{lang}", 0),
                    { File.Length: > 0 } => DownloadFiles.Entry(url, $"{host}/files/{Path.GetFileName(url.File)}"),
                    _ => UrlEntry(url.Type, url.Url, url.FileSize, url.Version, url.Md5),
                });
            }

        }
        else
        {
            entries.Add(UrlEntry("RANK_WEEK", $"{host}/ranking", 0));
        }

        // pes2011.exe FUN_00513de0 and pes2010.exe FUN_0072c6e0 never record the type,
        // version or size of ulist[0] (their guard is `index - 1 < 0x17`), only the url, so
        // slot 0 always reads as type 0 = RANK. Anything else put there becomes the ranking
        // url (PES 2010 posted its ranking query to /pcspec.bin; in PES 2011 a PC_SPEC there
        // made FUN_005a5d30 strlen(NULL) at 0x005A5D70), so slot 0 carries the RANK url.
        // ponytail: the slot-0 rule is verified only for PES 2010/2011; a RANK entry there is also a valid RANK entry if 2012 reads types.
        var rank = entries.FindIndex(e => e.GetString("type") == "RANK");
        var first = rank >= 0 ? entries[rank] : UrlEntry("RANK", $"{host}/ranking", 0);
        if (rank >= 0)
            entries.RemoveAt(rank);
        entries.Insert(0, first);

        return Reply.Of(ctx.Ok().SetList("url_num", "ulist", entries));
    }

    [Command("CMD_GET_INFORMATIONLIST", Roles = AnyServiceRole, RequiredState = SessionState.Connected)]
    public async ValueTask<KvMessage[]> GetInformation(CommandContext ctx)
    {
        var items = await repository.GetInformationAsync(ctx.CancellationToken);

        // The project notice is always first and always important; database items follow.
        // PES 2011 (FUN_005a72d0) pops an important item up only if its info_id is not in its
        // 30-slot "already seen" list (FUN_00537990). Empty slots hold 0, so id 0 always reads as
        // seen; a fresh id per request makes the notice pop up on every login. The time-based
        // id stays far above database identities.
        // In the language the client asked with (lang); see ServerText for the notice's terms.
        var lang = ctx.Request.GetString("lang") ?? ctx.Session.Language;
        var aboutId = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0x7fffffff);
        var entries = items.Select(i => InformationEntry(i.Id, i.PublishedAt, i.Subject, i.Body, i.Important, i.Present))
            .Prepend(InformationEntry(aboutId, DateTime.UtcNow, ServerText.Get("about.subject", lang),
                ServerText.Get("about.body", lang), important: true, present: true))
            .ToArray();

        return [ctx.Ok().SetList("info_num", "ilist", entries)];
    }

    private KvMessage InformationEntry(int id, DateTime date, string subject, string body, bool important, bool present)
        => new KvMessage()
            .Set("date", new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds())
            .Set("info_id", id)
            .Set("mes_subject", subject)
            .Set("mes_body", body)
            // PES 2011 reads important as enum 0x01 (FALSE|TRUE, FUN_0061d600); "1" is not a
            // name it knows and reads as not important. Other titles keep the reference "1"/"0".
            .Set("important", options.CurrentValue.GameProfile == GameProfile.Pes2011Pc
                ? (important ? "TRUE" : "FALSE")
                : (important ? "1" : "0"))
            .Set("present", present ? "1" : "0");

    internal static KvMessage UrlEntry(string type, string url, int size, int version = 1, string md5 = "0")
        => new KvMessage()
            .Set("type", type)
            .Set("url", url)
            .Set("version", version)
            .Set("file_size", size)
            // FUN_00513de0 reads md5 as a string; the bare 0 placeholder stays as the reference sent it.
            .Set("md5", md5 == "0" ? new KvRaw("0") : md5);
}

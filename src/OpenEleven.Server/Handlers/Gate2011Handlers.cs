// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenEleven.Data.Repositories;
using OpenEleven.Protocol.Crypto;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.Dispatch;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Handlers;

/// <summary>
/// The PES 2011 gate and login handshake: the first socket command, the gate's self-registration,
/// the terms text, and the two-stage login. Only pes2011.exe sends these; PES 2012 and later
/// log in through the shared handlers. Function addresses refer to <c>pes2011.exe</c> (PE32,
/// image base 0x00400000) and are recorded with their evidence in
/// <c>docs/reverse-engineering/pes2011-session-protocol.md</c>. The transport is the same as
/// PES 2010's (24-byte frame, XOR keystream, Blowfish key; <c>FUN_01006660</c>, <c>FUN_00feba00</c>).
/// </summary>
public sealed class Gate2011Handlers(
    GameIdTicketStore tickets,
    LoginThrottle throttle,
    IAccountRepository accounts,
    IOptionsMonitor<ServerOptions> options,
    Web.WebAssets webAssets,
    ILogger<Gate2011Handlers> log)
{
    /// <summary>The services the login handshake is known to run on.</summary>
    private const ServiceRole LoginRoles = ServiceRole.Gate | ServiceRole.Account | ServiceRole.Menu;

    /// <summary>
    /// The first command on every new game socket, sent right after <c>connect()</c> and before
    /// any gate or account traffic: <c>FUN_0050d880</c> registers the name with
    /// <c>FUN_0051fc00("NETCLIENT")</c>, and <c>FUN_005228a0</c> only advances its state
    /// machine once the reply arrives. Empty body; a bare NOERR envelope is the whole contract.
    /// </summary>
    [Command("NETCLIENT", Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> NetClient(CommandContext ctx) => Reply.Of(ctx.Ok());

    /// <summary>
    /// The client's self-registration: <c>FUN_00512320</c> builds
    /// <c>server_num=1,server_list=[{svrtype="GATE",svraddr="pes11pcgate-e.winning-eleven.net",
    /// svrport=4010,userCount={now=1,min=0,max=10}}]</c> (the port comes from the immediate
    /// <c>0xfaa</c> in that function) and expects a bare acknowledgement. The advertised
    /// address is the client's own idea of where the gate lives, so it is logged and not stored.
    /// </summary>
    [Command("CMD_REG_SVRLIST", Roles = ServiceRole.Gate, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> RegisterServerList(CommandContext ctx)
    {
        log.LogDebug(
            "PES 2011 gate self-registration from {Session}: svrtype={SvrType}",
            ctx.Session, ctx.Request.GetString("svrtype"));

        return Reply.Of(ctx.Ok());
    }

    /// <summary>
    /// The terms screen (FUN_00604880) asks for the EULA_PES url. The retail client turns this
    /// command into its own HTTP GET (request object FUN_011beb10) and wraps the body into
    /// <c>read_buffer</c>, so the /eula_pes.txt endpoint is what really answers; this socket
    /// handler gives the same text for a client that does send it here.
    /// </summary>
    [Command("CMD_GET_HTTP", Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public ValueTask<KvMessage[]> GetHttp(CommandContext ctx)
    {
        var uri = ctx.Request.GetString("uri") ?? "";
        var path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.AbsolutePath : uri;

        string text;
        if (path.EndsWith("/eula_pes.txt", StringComparison.OrdinalIgnoreCase) || path.Length == 0)
        {
            var lang = parsed is null ? null : System.Web.HttpUtility.ParseQueryString(parsed.Query)["lang"];
            text = Web.GameEndpoints.EulaText(options.CurrentValue, lang ?? ctx.Session.Language);
        }
        else
        {
            text = webAssets.TryGet(path, out var content) ? System.Text.Encoding.UTF8.GetString(content) : "";
        }

        log.LogInformation("CMD_GET_HTTP uri {Uri}: {Length} characters", uri, text.Length);
        return Reply.Of(ctx.Ok().Set("read_buffer", text));
    }

    /// <summary>
    /// Stage one of the login: trade <c>gameid</c>/<c>gameid_password</c> for an opaque
    /// <c>loginid</c>/<c>loginid_password</c> pair (<c>FUN_0061c360</c> state 2, sender
    /// <c>FUN_006425c0</c>). Field names are verified; <c>password_type</c> carries
    /// <c>PASS_PLAIN</c> or <c>PASS_AES</c>.
    /// </summary>
    /// <remarks>
    /// <c>PASS_PLAIN</c> is checked against the account's registered digest, the same one the
    /// web registration form stores (<c>AuthProof.HashPassword</c>). <c>PASS_AES</c> is refused
    /// rather than guessed: the key that would unwrap <c>gameid_password</c> has not been
    /// recovered, so accepting the value would mean trusting a ciphertext as a password.
    /// </remarks>
    [Command("CMD_AUTH_GAMEID", Roles = LoginRoles, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public async ValueTask<KvMessage[]> AuthGameId(CommandContext ctx)
    {
        var gameId = ctx.Request.GetString("gameid")?.Trim();
        var password = ctx.Request.GetString("gameid_password");
        var passwordType = ctx.Request.GetString("password_type") ?? "PASS_PLAIN";

        if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(password))
        {
            log.LogWarning("CMD_AUTH_GAMEID from {Session} is missing gameid or gameid_password", ctx.Session);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        if (!string.Equals(passwordType, "PASS_PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "CMD_AUTH_GAMEID from {Session} used unsupported password_type {Type}",
                ctx.Session, passwordType);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        if (!throttle.TryAttempt(ctx.Session.Remote.Address))
        {
            log.LogWarning("CMD_AUTH_GAMEID from {Session} refused: too many login attempts", ctx.Session);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var account = await accounts.GetByGameIdAsync(gameId, ctx.CancellationToken);
        if (account is null)
        {
            log.LogWarning("CMD_AUTH_GAMEID for unknown game id {GameId}", gameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        if (account.Banned)
        {
            log.LogWarning("Banned account {GameId} tried CMD_AUTH_GAMEID", account.GameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var presented = AuthProof.HashPassword(password);
        if (!AuthProof.TryNormalizeMd5(account.PasswordHash, out var registered)
            || !AuthProof.FixedTimeEquals(registered, presented))
        {
            // No detail beyond this: the account exists and the password is wrong.
            log.LogWarning("CMD_AUTH_GAMEID failed the password check for {GameId}", account.GameId);
            return [ctx.Fail("ERR_AUTHGID_AUTH")];
        }

        var (loginId, loginPassword) = tickets.Mint(account.Id, account.GameId);

        log.LogInformation("CMD_AUTH_GAMEID issued a login ticket for account {GameId}", account.GameId);

        return
        [
            ctx.Ok()
                .Set("loginid", loginId)
                .Set("loginid_password", loginPassword),
        ];
    }

    /// <summary>
    /// Stage two of the login: <c>CMD_LOGIN</c> replays the pair minted by
    /// <c>CMD_AUTH_GAMEID</c> (<c>FUN_0061c360</c> state 7, sent from <c>FUN_006425c0</c>).
    /// On success the session becomes <see cref="SessionState.Authenticated"/> and the client
    /// moves on to <c>CMD_GET_PLAYERLIST</c> and <c>CMD_SET_LANGUAGE</c>, which the shared
    /// handlers already serve.
    /// </summary>
    /// <remarks>
    /// Only <c>account_first_login</c> is returned, because it is the only field of this
    /// response the binary is known to read back (<c>FUN_0061c360</c> state 8, literal at
    /// 0x0130c9b8). <c>accountid</c> does not exist as a literal anywhere in pes2011.exe, so it
    /// is deliberately not invented here.
    /// </remarks>
    [Command("CMD_LOGIN", Roles = LoginRoles, Profiles = GameProfile.Pes2011Pc, RequiredState = SessionState.Connected)]
    public async ValueTask<KvMessage[]> Login(CommandContext ctx)
    {
        var style = ctx.Request.GetString("style");
        if (style is not null && !string.Equals(style, "LOGINID", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("CMD_LOGIN from {Session} used unsupported style {Style}", ctx.Session, style);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        var ticket = tickets.Redeem(
            ctx.Request.GetString("loginid"),
            ctx.Request.GetString("loginid_password"));

        if (ticket is null)
        {
            log.LogWarning(
                "CMD_LOGIN from {Session} presented a login id the server did not mint or has expired",
                ctx.Session);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        var account = await accounts.GetByGameIdAsync(ticket.GameId, ctx.CancellationToken);
        if (account is null || account.Id != ticket.AccountId)
        {
            log.LogWarning("CMD_LOGIN ticket named account {AccountId} which no longer exists", ticket.AccountId);
            return [ctx.Fail("ERR_INVALID_ACCOUNT")];
        }

        if (account.Banned)
        {
            log.LogWarning("Banned account {GameId} tried CMD_LOGIN", account.GameId);
            return [ctx.Fail("ERR_LOGIN_FAILED")];
        }

        // use_regcode/regcode are verified field names; when the client asks for the serial to
        // be checked the comparison is against the serial registered for the account, exactly
        // as the PES 2010 path does. use_regcode="NO" skips it.
        if (IsYes(ctx.Request.GetString("use_regcode")))
        {
            var presentedRegCode = ctx.Request.GetString("regcode")?.Trim().ToUpperInvariant();
            var registeredRegCode = account.RegCode?.Trim().ToUpperInvariant();

            if (string.IsNullOrEmpty(registeredRegCode))
                return [ctx.Fail("ERR_REGCODENOTISSUED")];

            if (!string.Equals(registeredRegCode, presentedRegCode, StringComparison.Ordinal))
            {
                log.LogWarning(
                    "CMD_LOGIN for {GameId} presented a serial that does not match the registered one",
                    account.GameId);
                return [ctx.Fail("ERR_REGCODE_FAILED")];
            }
        }

        ctx.Session.AccountId = account.Id;
        ctx.Session.GameId = account.GameId;
        ctx.Session.RegCode = account.RegCode ?? "";
        ctx.Session.FirstLogin = await accounts.ClaimFirstLoginAsync(account.Id, ctx.CancellationToken);
        ctx.Session.State = SessionState.Authenticated;

        await accounts.TouchLoginAsync(account.Id, ctx.CancellationToken);

        log.LogInformation(
            "Account {GameId} logged in over PES 2011 CMD_LOGIN (first login: {FirstLogin})",
            account.GameId, ctx.Session.FirstLogin);

        return [ctx.Ok().Set("account_first_login", ctx.Session.FirstLogin)];
    }

    /// <summary>
    /// Protocol-wide booleans are the strings YES/NO (pes2011.exe enum type id 0x4c), but a
    /// leading 1 is accepted too so a client that sends the numeric form is not rejected.
    /// </summary>
    private static bool IsYes(string? value)
        => value is not null
           && (value.Equals("YES", StringComparison.OrdinalIgnoreCase) || value == "1");
}

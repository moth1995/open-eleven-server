# OpenEleven security audit - third pass

- **Date:** 2026-10-25
- **Revision:** `6a6035c` plus the uncommitted admin/lobby/public-site working tree (see section 9)
- **Previous pass:** 2026-10-25, second pass (same document)
- **First pass:** 2026-10-24 at `d9dae3d`
- **Scope:** all tracked source under `src/` (Protocol, Data, Server, Profiles, the four title
  executables), the public site, admin portal and lobby editor under `Pages/` and `Web/`,
  `conf/*.yaml`, `docker/`, `README.md`, and repository hygiene.
- **Out of scope:** `main.py`, `tools/` and the Ghidra/Cheat Engine MCP wiring are local-only and
  untracked. No dynamic testing, fuzzing, packet captures or dependency CVE scan were performed.
- **Method:** static review, manual data-flow tracing, re-verification of every finding from the
  earlier passes, and a full read of the new lobby, live-refresh and public match-detail code.

> This pass re-checks the fixes landed since the second pass and reviews the newly added admin
> lobby editor, live settings for lobbies, per-title admin credentials, public match details and
> the live-refresh script. Findings keep stable IDs. `docs/security-audit.md` is the living report;
> the history is preserved below rather than rewritten.

---

## 0. Third-pass delta

| Change | Review |
|---|---|
| Admin lobby editor (`Pages/Admin/Lobbies.*`) and saved lobby list (`LiveSettings.LobbyPrefix`, `ReplaceLobbies`) | Inside the authorized `/Admin` folder, validated by the same options validator, stored under a separate `lobbies:` prefix (not the `config:` allowlist), applied only at the next start. No new access-control issue; names are capped at 32 and max players at 1..1000. |
| Per-title admin credentials (`ADMIN_PASSWORD_PES20xx` in compose/.env) and README security section | Closes the first A-4 deployment decision; `chmod 700 data`, root-only `.env` and the 600 cert hook are documented. `conf/*.yaml` permissions are still not mentioned (see 5.1). |
| Public match details (`_MatchDetail`, `SiteData.Events`), page sizes (`size=25/50/100`), pagination parameter rename (`p`), live refresh (`wwwroot/js/live.js`) | Event and match text is Razor-encoded, page sizes are allowlisted, and the refresh is same-origin, skips redirected responses (expired admin session) and only swaps `[data-live]` sections. No new vulnerability. L-6 now covers event and lobby text. |
| New tests | `dotnet test` passes: 309 server tests + 63 protocol tests, 0 failures. |
| A-4 ticket residual | Still present: a password reset does not revoke cached PES 2011 tickets (see A-4 in section 4). |

---

## 1. Status since the first pass

| First-pass ID | Status | Evidence |
|---|---|---|
| H-1 credential logging | **Closed** | `f978bf0`, `c2f292e` - hash/body logs removed, `/gameid_auth` marked `[SensitiveBody]`, KV fields redacted (`PacketLogFormatter.cs:37-78`, `GameEndpoints.cs:106-135`) |
| H-2 zlib bomb | **Closed** | `e603a4e` - 1 MiB ceiling plus bounded decompression (`InnerBodyChunks.cs:22-47,81-98`) |
| H-3 KV stack overflow | **Closed** | `ac18399` - depth counter, limit 32 (`KvReader.cs:18-19,47-50`) |
| M-1 no throttling | **Reduced to Low** | `2d7099f` (20/min per address, shared by both login paths), `5e1eebf` (HTTP limits) |
| M-2 any member can forge matches | **Reclassified Low** | `1d45eb8` gates four event commands on the owner; maintainer review shows the client reports its own side's events, so the gate risks dropping legitimate away-side updates. See M-2 |
| M-3 PID IDORs | **Reclassified Low** | `a983d4b` fixes the two destructive notice/division reads; roster and social scoping still need a capture. See M-3 |
| M-4 PES 2011 serial bypass | **Open, re-graded Low** | `Pes2011Handlers.cs:713-770` unchanged in substance |
| L-1 ticket binding | Open | `GameIdTicketStore.cs:43-60` |
| L-2 unsalted MD5 | Accepted (protocol-bound) | `AuthProof.cs:44-45` |
| L-3 EULA path traversal | **Closed** | `7b90713` (`GameEndpoints.cs:156-164`) |
| L-4 container hardening | **Partially fixed** | MySQL pinned to 8.4, healthcheck password removed (`cebd1c4`); still runs as root |
| L-5 ranking query DoS | **Reduced** | 4 KiB body cap and 60/min per address (`GameEndpoints.cs:69-80`) |
| L-6 text hot spots | Open | `MailHandlers.cs:182-188`; event and lobby text (section 4) |
| L-7 identity adoption | Open (off by default) | `Hub.cs:120-156` |
| L-8 misc hardening | Partially fixed | EULA fixed; log/header items remain |

**Maintainer review (2026-10-25).** Two first-pass grades were corrected in this pass. M-2 was
argued on the premise that only the room owner may report match events; the maintainer's captures
show each client reports its own side, so the owner-only gate added in `1d45eb8` can silently drop
the away player's goals/cards/combinations. M-3 assumed a foreign room or PID in a watch request is
never legitimate; the roster watch exists to update everyone in the room, including spectators, so
the finding is now about scope verification and endpoint disclosure rather than a broken access
control. Both corrections are recorded here rather than silently edited, so the reasoning is
auditable. A-1 was withdrawn entirely after a runtime matrix showed Razor Pages already validates
the token per POST; the original claim was wrong.

Also new in this window and worth noting: `8b8b973` changed the default command state to
`Authenticated`, closing the broad class of pre-login command exposure that the first pass
described. That was the right systemic fix.

**H-1 was also re-graded on review.** The maintainer's assessment is accepted: an actor who can
read the server log in this deployment can normally read the database or the host, so the digest
did not grant a new privilege in the common threat model. It is closed regardless because the PES
2011 path logged a *plaintext* password (which the database does not contain), and the redaction
costs nothing. Keep `PacketLogFormatter.Redact` and the `[SensitiveBody]` attributes as defense in
depth, not as a release gate.

## 2. Executive summary

There are **no open High or Medium findings**. The two trivial remote crashes and the credential
logging are fixed correctly, with tests. The new admin portal is, on the whole, well designed:
folder-level authorization, a single hardened cookie, fixed-time credential comparison, rate
limiting, 404 when disabled, and no raw HTML output.

The two game-protocol items I graded Medium in the first pass are reclassified after maintainer
review:

1. **M-2 (Low)** - the event stream is participant-scoped by design: each client reports its own
   side's goals, cards and combinations, so the owner-only gate from `1d45eb8` is a functional
   regression risk (away-side updates are acknowledged then discarded). What remains is narrower:
   make score updates idempotent, and stop `CMD_SET_INJURYGAMEEND` from settling a result outside a
   running match.
2. **M-3 (Low)** - returning a room roster to its members, including spectators, is intended. The
   open question is scope (bind the watch to the caller's room) and whether peer endpoints should
   be included for non-participants.

The admin-portal items from the earlier pass are closed except the A-4 ticket residual. Razor Pages
validates the antiforgery token on every POST by itself; `UseAntiforgery()` is the minimal-API path
and is deliberately absent so it cannot block `/gameid_auth` (a runtime matrix and a test pin this).
The live-settings layer now accepts only catalog keys and validates without touching other threads,
a password reset revokes the account's web logins and sockets, and search/cache size is bounded.
What remains of A-4 is one small code gap (PES 2011 tickets are not revoked on reset) and small
hardening notes (see 5.1).

## 3. Open findings

| ID | Severity | Finding |
|----|----------|---------|
| M-2 | Low | Match events are participant-scoped: the owner-only gate risks dropping away-side reports; settlement lacks state validation |
| M-3 | Low | Roster and social-data scope: bind watches to the caller's room/PID; do not hand peer endpoints to non-participants |
| M-1 | Low | Login throttling exists but is per-IP only; no account lockout; captcha still off by default |
| M-4 | Low | PES 2011 serial check is client-controlled; `CMD_AUTH_GAMEID` skips the pending-login gate |
| A-4 | Low | Password reset revokes web logins and sockets, but PES 2011 tickets stay redeemable until their TTL |
| L-1 | Low | PES 2011 login tickets are neither IP-bound nor single-use |
| L-4 | Low | Containers still run as root (documented, not fixed); no read-only rootfs |
| L-6 | Low | O(n^2) mail clipping; unbounded friend-request, profile, match-event and lobby text |
| L-7 | Low | `ShareIdentityByRemoteAddress` is an auth bypass when enabled |
| L-8 | Low | Verbose packet logging by default; log injection; no HSTS/security headers |

A-1..A-3 and A-5 are closed (section 5). A-4 is closed for web logins and sockets, but leaves a
PES 2011 ticket-revocation gap (section 4) and small hardening notes (5.1).

---

## 4. Findings

### M-2 Low (was Medium) - Match events are participant-scoped, not owner-scoped

**Correction.** The first pass recommended gating in-match reports on the room owner, and `1d45eb8`
implemented that for `CMD_CHANGE_GAMEPHASE`, `CMD_ADD_SCORE`, `CMD_ADD_FOUL` and
`CMD_UPDATE_COMBINATION` through `IsMatchHost` (`Handlers/Pes2011Handlers.cs:310-318`). Maintainer
review shows that premise is wrong: in a P2P match each client reports its own side's events, so the
away player's client sends the away goal (and its own cards and combinations), not the host on
everyone's behalf. The reverse-engineering notes agree that the setup commands are "(any member)"
(`docs/reverse-engineering/pes2011-session-protocol.md:741`), and the in-match event table lists the
fields per client driver without naming the room owner (`:1073-1084`).

**Impact, functional first.** With the owner-only gate, a legitimate away-side `CMD_ADD_SCORE`,
`CMD_ADD_FOUL` or `CMD_UPDATE_COMBINATION` is acknowledged and silently discarded. The score, card
list and combination totals in the match record are then wrong, and an away-side
`CMD_CHANGE_GAMEPHASE` is ignored. The test at
`tests/OpenEleven.Server.Tests/Pes2011SocialTests.cs:138-162` pins the owner-only model, so it
cannot catch this. The user-visible failure is worse than the integrity concern it was meant to
fix.

**What still needs tightening.** The boundary to enforce is room participation, not ownership:

| Command | Location | Correct scope / remaining gap |
|---|---|---|
| `CMD_ADD_SCORE` | `Pes2011Handlers.cs:240-267` | accept from either participant; **increments per report**, so two peers reporting one goal count it twice (the notes suggest the client can echo running totals, `:1122`) |
| `CMD_CHANGE_GAMEPHASE` | `Pes2011Handlers.cs:183-221` | accept from either participant; `END` settles the result, so require `Status == "GAME"` |
| `CMD_ADD_FOUL` | `Pes2011Handlers.cs:273-287` | accept from either participant |
| `CMD_UPDATE_COMBINATION` | `Pes2011Handlers.cs:290-302` | credit the sender (already does); accept from either participant |
| `CMD_SET_INJURYGAMEEND` | `Pes2011Handlers.cs:346-355` | **settles from any state**, including `WAITING`/`ENTRY` and before both sides exist; require `GAME` + both `GameSide`s |
| `CMD_SET_GAMEMEMBERENV` | `Pes2011Handlers.cs:157-176` | the client's error table contains `ERR_NOTGAMEHOST` (`:923`), suggesting the retail server does gate this one; verify, then return that error instead of silently accepting |
| `CMD_SET_GAMETEAM`, `CMD_SET_GAMEENV` | `Pes2011Handlers.cs:224-234`, `RoomHandlers.cs:156-184` | participants configure teams/settings per the notes; keep membership, not ownership |
| `CMD_UPDATE_ROOMSTATE` | `RoomHandlers.cs:341-372` | any member can flip `WAITING`/`GAME`/`RESULT`; if the client drives this, validate allowed transitions per current status |
| `CMD_SEND_ABNORMALEND`, `CMD_SET_FORFEITEDGAMEEND` | `Pes2011Handlers.cs:361-374`, `:325-340` | genuine disconnect signals from either player; require `GAME`, and only void once the peer's socket actually dropped |

The narrow integrity gap that remains is `CMD_SET_INJURYGAMEEND`: it writes a completed match
(`State/MatchResults.cs:105-173`) from any room state, so a client can settle a 0-0 draw before the
match exists, or lock in the current score on demand. That is a state-validation bug, not an
ownership bug.

**Recommendation.** Replace the owner gate on the four event commands with a participant check
(member of the room with an assigned `GameSide`); make `CMD_ADD_SCORE` idempotent (set the total, or
de-duplicate by scorer + phase); require `room.Status == "GAME"` and both sides present before any
settlement; keep the disconnect paths available to both players but verify the peer actually
dropped. Rewrite the owner-only test as a participant/spectator matrix, and settle
`CMD_SET_GAMEMEMBERENV` from a capture using the `ERR_NOTGAMEHOST` evidence.

### M-3 Low (was Medium) - Roster and social-data scope needs verification

**Correction.** The roster watch is a subscription, not a one-off read: the client subscribes with
its room's `room_id` after creating or joining (`docs/reverse-engineering/pes2011-session-protocol.md:732`),
and the server pushes updates so everyone in the room, including a spectator of the match, sees the
current roster. Returning the roster is therefore intended, and `CMD_GET_IPANDPORT` is the host
lookup a joining client needs (`:893-898`). The first pass assumed a foreign room or PID is never
legitimate; that assumption was not supported by the notes.

**What remains worth checking is scope, not existence:**

- `CMD_REQWATCHL_ROOMPLAYERLIST` (`Handlers/Pes2011Handlers.cs:544-551`) takes `room_id` from the
  request with no check that the caller is in (or watching) that room. If the client only ever
  subscribes to its own room, bind the handler to `ctx.Session.RoomId` and ignore the field; if
  arbitrary-room watching is a real feature, keep it but return `ex_ip`/`in_ip`/ports only to
  participants (`State/Pes2011Lists.cs:101-112`). A spectator that never opens a P2P connection
  has no reason to receive the players' addresses.
- `CMD_GET_FRIENDLIST` and `CMD_GET_BLACKLIST` (`Handlers/FriendHandlers.cs:33-66,142-152`) accept a
  foreign `target_pid`/`pid`. If the client always sends its own, bind them to `ctx.Session.Pid`; if
  viewing another player's profile is intended, cap and document it. A capture settles which.
- `CMD_GET_IPANDPORT` (`Handlers/RoomHandlers.cs:90-117`) is block-scoped by design. The client
  needs the host's endpoint before joining, so treat it as an accepted trade-off and note it in the
  deployment docs.

**Recommendation.** Confirm the client's request scope with a capture, then bind each handler to it.
Keep the `a983d4b` own-PID checks for the destructive notice/division reads, which were genuine
IDORs because they consumed another player's data.


### M-1 Low (was Medium) - Throttling exists, but is per-address only

`State/LoginThrottle.cs:19-33` allows 20 password checks per minute per source address and is
shared by `/gameid_auth` and `CMD_AUTH_GAMEID` (`Web/GameIdAuthService.cs:28-29`,
`Pes2011Handlers.cs:659-663`). HTTP registration is 10/hour and `/ranking` 60/min per address
(`Web/HttpRateLimits.cs:30-37`). That closes the previous trivial brute-force path.

Residual risk: no per-account counter, so a distributed source or a botnet can still spread
guesses; a shared NAT (LAN party, CGNAT) gives every player one 20/min budget; and the captcha
still defaults to `None` (`Configuration/ServerOptions.cs:286`, all shipped configs), so account
creation is only rate-limited, not challenged.

**Recommendation.** Add a per-account failure counter with exponential backoff (independent of
source address), enable captcha in the production sample and Docker guidance, and consider
alerting after repeated failures for one Game ID.

### M-4 Low (was Medium) - PES 2011 serial check is client-controlled

`CMD_LOGIN` still only checks the serial when the client sends `use_regcode=YES`
(`Pes2011Handlers.cs:747-770`); a modified client sends `NO`. `CMD_AUTH_GAMEID` still authenticates
on Game ID and password alone, without the HTTP pending-login gate that binds the PES 2010 path to
the source address and challenge proof (`Pes2011Handlers.cs:638-698` vs
`Handlers/AccountHandlers.cs:46-155`).

Re-graded because a password is still required, throttled, and the serial is a licensing control
rather than an authentication factor. It remains a deliberate weakening of the 2011 posture.

**Recommendation.** Reject `use_regcode=NO` when the account has a registered serial. Require a
pending HTTP login (or the challenge proof) before minting tickets, so both titles share one gate.

### A-4 (residual) Low - PES 2011 tickets survive a password reset

The admin password reset now calls `PendingLoginStore.Revoke` and `hub.Disconnect`
(`Pages/Admin/Account.cshtml.cs:80-85`), which ends the pending web login and every live socket.
`GameIdTicketStore`, however, has no revoke-by-account: a PES 2011 client that already cached a
`loginid`/`loginid_password` pair can reconnect and redeem it through `CMD_LOGIN` until the ticket's
`PendingLoginLifetimeSeconds` expires (300 s by default), re-establishing the session the reset was
meant to end. The window is short and the ticket is 128-bit random, so this is Low, not a bypass of
the reset in normal use.

**Recommendation.** Add `GameIdTicketStore.Revoke(int accountId)` (a scan over `_tickets` is cheap at
this size) and call it next to `Revoke` in the reset handler; alternatively stamp the ticket with the
account's password hash and reject it in `Redeem` when the stored hash changed. A test can mirror
`A_password_reset_ends_the_accounts_web_logins`.

**Also under A-4:** the admin cookie still has an 8-hour sliding expiry with no absolute lifetime
and no `__Host-` prefix (`Web/AdminAuth.cs:37-38`), so a stolen cookie stays valid as long as it is
used. Low-risk hardening: add an absolute lifetime and the prefix when HTTPS is on.

The other four admin-portal findings (A-1..A-3, A-5) are **closed**. See section 5 for the evidence
and 5.1 for the remaining A-4 deployment note.

### L-1 Low - PES 2011 tickets are not IP-bound or single-use

`State/GameIdTicketStore.cs:43-60` mints reusable 128-bit pairs for
`PendingLoginLifetimeSeconds` with no source-address binding. A ticket captured from memory, a log
(now redacted) or plaintext traffic is replayable from any host until expiry. Reuse is intentional
because the client replays cached pairs; binding is not.

**Recommendation.** Store the minting address and check it on `Redeem`; allow reuse only for a short
window, then mark consumed if the protocol tolerates it.

### L-4 Low - Containers still run as root

`docker/Dockerfile:24-33` has no `USER`, so the service runs as root and the compose file now
documents it (`docker/docker-compose.yml:10-12`). MySQL is pinned (`mysql:8.4`) and the healthcheck
no longer passes the root password on the command line, which are real improvements. No read-only
rootfs, `no-new-privileges`, or capability drops.

**Recommendation.** Add a non-root user and `USER` (the data/log volumes are already isolated),
`read_only: true` with a writable `/app/data` and `/app/logs`, and
`security_opt: ["no-new-privileges:true"]`.

### L-6 Low - Text handling hot spots

`MailHandlers.Clip` still trims one character at a time while measuring the whole string
(`Handlers/MailHandlers.cs:182-188`), so a ~100 KB body is O(n^2) per call. `CMD_SEND_FRIENDREQ`
still stores an unbounded `mes_body` with no control-character filtering
(`Handlers/FriendHandlers.cs:90-96`), and `CMD_SET_PLAYERPROFILE` accepts unbounded `intro`, `lang`,
`selfreport_level` and `position_want` (`Handlers/AccountHandlers.cs:407-410`), all of which are
returned to other clients. With the new public match page, two more sources now reach a rendered
page: `CMD_ADD_FOUL` stores `card_kind`/`injury_side` verbatim in `MatchRecord.Events`
(`Handlers/Pes2011Handlers.cs:273-287`), which `SiteData.Events` parses for `/matches`, and the
lobby editor accepts lobby names without a control-character check
(`Pages/Admin/Lobbies.cshtml.cs:47-58`). A host can therefore put misleading text (or `|`/newline
fragments) into its own match detail, and an admin can put control characters into a lobby name.

**Recommendation.** Truncate on a byte boundary without rescanning; apply a length cap and
`char.IsControl` rejection to the notice fields, profile strings, event fields and lobby names.
The event parser should also drop fields it does not recognise rather than displaying them.

### L-7 Low - `ShareIdentityByRemoteAddress`

`Hub.TryAdoptIdentity` (`State/Hub.cs:120-156`) copies `AccountId`, `Pid` and the login proof from
an authenticated session with the same source address, with no credential. Off by default and
documented as unsafe, but one config edit turns a shared NAT into account takeover.

**Recommendation.** Delete the option, or refuse to start with it enabled outside a development
configuration. At minimum, log a startup warning.

### L-8 Low - Logging, headers, and defaults

- `Debug.HexDump` still defaults to `true` (`Configuration/ServerOptions.cs:408`) and the shipped
  title configs enable it. Credentials are now redacted, but full packet payloads still put private
  chat, player names and Game IDs in the log at Information level. Consider defaulting it to
  `false` and making it an explicit reverse-engineering switch.
- `game_id` and the admin user name are logged unvalidated (`GameEndpoints.cs:119-121`,
  `Pages/Admin/Login.cshtml.cs:34`), so newlines and ANSI escapes can forge log lines. Reject
  control characters on both paths.
- No HSTS and no security headers on the Razor surface; `conf/server.yaml` still ships
  `Https.Enabled: false` (local template).
- `EnforceServiceRoles` remains `false` (`ServerOptions.cs:386`), so `Roles` is documentation only;
  the new `Authenticated` default is what actually protects commands. If per-service separation
  matters, enable it and fix whatever legitimate cross-port traffic it exposes.

---

## 5. Closed and accepted

| ID | Outcome | Notes |
|---|---|---|
| H-1 | Closed | `f978bf0`, `c2f292e`. Hash/body logs removed, `/gameid_auth` and the admin password form are `[SensitiveBody]`, KV secrets are masked. Re-graded: log-reader access is not a release blocker per maintainer, and the plaintext 2011 password is gone. Keep the tests. |
| H-2 | Closed | `e603a4e`. Inflated size is capped at 1 MiB and decompression writes into a fixed buffer; overflow past the declared size throws. |
| H-3 | Closed | `ac18399`. `MaxDepth = 32` threaded through fields, records and lists. A fuzz test asserting only `ProtocolException` escapes would make this permanent. |
| L-2 | Accepted | Unsalted single-round MD5 is forced by the client. Document it and protect the database as a credential store. |
| L-3 | Closed | `7b90713`. `lang` must be 2-3 ASCII letters before it enters a file name. |
| L-5 | Reduced | 4 KiB request cap and 60/min per address. Still a full-table scan per miss; the page-level caching and A-5 changes cover the rest. |
| A-1 | **Withdrawn** | My premise was wrong: Razor Pages validates the antiforgery token on every POST handler by itself, and `UseAntiforgery()` is for minimal APIs. Runtime matrix: token-less admin login/settings/register POST -> 400; the same POSTs with a token -> 302; `/gameid_auth` (no `[FromForm]`, no token) -> 200. `AdminPortalTests.No_page_opts_out_of_antiforgery` pins that no page opts out, and `GameEndpoints.cs:101-105` documents why the middleware stays out. |
| A-2 | Closed | `LiveSettings.Unlisted` restricts `Check`/`LoadAsync`/`SaveAsync` to `LiveSettingCatalog` keys; a forged `config:Server:Admin:Password` row is ignored with a warning that names only the key, and saved rows for a lobby server.yaml no longer has are dropped. Test: `Only_catalog_settings_are_accepted_or_loaded`. |
| A-3 | Closed | Validation uses an `AsyncLocal` candidate (`LiveSettings.cs:24-42`); other threads keep reading `Data`. Test: `A_change_being_checked_is_invisible_to_other_threads`. |
| A-4 | **Partially closed** | Password reset now calls `PendingLoginStore.Revoke` and `hub.Disconnect` (`Pages/Admin/Account.cshtml.cs:80-85`); test `A_password_reset_ends_the_accounts_web_logins`. Residual: PES 2011 tickets are not revoked (section 4). Per-title admin credentials are in place; see 5.1 for the remaining `conf` permission note. |
| A-5 | Closed | `PlayersModel.MaxSearchLength = 32` (the form's limit) and the output cache `SizeLimit` is 16 MB instead of the 100 MB default. |

### 5.1 A-4 deployment decisions (status)

The first operational part is done; the other two are documentation/hardening notes.

1. **One admin credential per title - done.** The compose file now passes
   `ADMIN_PASSWORD_PES2010`..`ADMIN_PASSWORD_PES2013` to the four services, and the README says to
   use a different password for each title. A leak opens one portal, not four. Keep the variables
   per title if the deployment is copied or scripted.
2. **Protect the secret files and the key ring - partially done.** The README now documents
   `chmod 700 data`, `docker/.env` as root-only, and the certbot hook writes `privkey.pem` at mode
   600. Add one line for `conf/server.<title>.yaml` (mode 600): it holds that title's admin
   password in plaintext, and on a multi-user host the default 644 is readable by everyone.
3. **Do not put the password in the tracked YAML.** `conf/server.pes20xxpc.yaml` is tracked with an
   empty `Admin.Password`, and the README says "in the title's YAML". Prefer `docker/.env`
   (`ADMIN_PASSWORD_PES20xx`) or a gitignored `server.local.yaml`; editing the tracked file and
   committing it would publish the password. Worth spelling out in the admin section.

No code change is needed for any of these; `README.md` is the right place, next to the existing
volume instructions.

## 6. Admin portal review (new surface)

The portal is a single-operator back office at `/admin` with accounts, bans, kicks, password
resets, news, live settings and the lobby editor.

**Controls that are correct**

- Folder authorization plus an explicit anonymous login page (`OpenElevenServerHost.cs:67-70`), and
  a `404` for every admin page while the portal is disabled (`Web/AdminAuth.cs:80-93`). The new
  `/admin/lobbies` page inherits both.
- Cookie: `HttpOnly`, `SameSite=Strict`, `Secure` when HTTPS, scoped to `/admin`, 8 h sliding
  (`Web/AdminAuth.cs:28-38`).
- Fixed-time user and password comparison; both are evaluated (`Web/AdminAuth.cs:66-78`).
- A hash "stamp" claim invalidates every session when the user or password changes
  (`Web/AdminAuth.cs:40-47,76-77`).
- Login rate limit (10 POSTs / 5 min per address, `Pages/Admin/Login.cshtml.cs:16-17`) and
  `[SensitiveBody]` so the form never reaches the trace log.
- `LocalRedirect` guarded by `Url.IsLocalUrl` and an `/admin` prefix, so no open redirect
  (`Pages/Admin/Login.cshtml.cs:42`).
- No `Html.Raw`, `MarkupString` or `HtmlString` anywhere in `Pages/`; all operator and player text
  is Razor-encoded, including attribute values.
- Ban and kick audit-logged and effective immediately (`Hub.Disconnect`); password reset reuses the
  registration policy (`RegistrationEndpoint.ValidatePassword`) and now revokes the account's
  pending web logins and live sockets (`PendingLoginStore.Revoke` + `Hub.Disconnect`).
- Live settings are validated through the same options validator before they are applied and
  stored, are restricted to `LiveSettingCatalog` keys (`LiveSettings.Unlisted`), and validate in an
  `AsyncLocal` view so another thread never observes an unsaved candidate
  (`Configuration/LiveSettings.cs:24-53,80-85`). Security-critical sections (Admin, HTTPS,
  database, ports, captcha) are not in the catalog.
- The lobby editor uses a separate `lobbies:` prefix, not the `config:` allowlist: it stores the
  whole list (max 10 names of 32 characters, 1..1000 players), validates it with the same options
  validator, and applies it only at the next start, so a save cannot renumber the blocks players
  are standing in (`Pages/Admin/Lobbies.cshtml.cs:47-58,80-88`,
  `Configuration/LiveSettings.cs:174-210`).
- `wwwroot/js/live.js` fetches only the same URL with same-origin credentials, ignores redirected
  responses (an expired admin session), does not run while the tab is hidden, and only swaps
  elements marked `data-live`. It parses the server's own HTML, which is Razor-encoded throughout.
- Public match details (`Pages/Shared/_MatchDetail.cshtml`) render stored event lines as encoded
  text; page sizes are allowlisted (`SiteData.Size`) and the pagination parameter is `p`, avoiding
  the route value Razor Pages reserves.

All portal findings from the earlier pass are closed except the A-4 ticket residual (section 4); the
remaining A-4 items are the cookie hardening note and a deployment documentation note (5.1). The
portal also gained a test that no page opts out of antiforgery.

## 7. Controls that are working

- **No SQL injection surface.** All request-driven queries are LINQ/EF; the raw statements in
  `Data/DatabaseInitializer.cs` use model-derived identifiers, and metadata lookups are parameterized.
- **No unsafe deserialization or process execution.** No `BinaryFormatter`, no `Process.Start`, no
  input-driven `Assembly.Load`.
- **Parser limits now exist on both axes** (depth and inflated size) and are reached before any
  authentication check, which is the right place to defend them.
- **Credential handling in transit and at rest in logs**: secrets are masked in both the pretty
  printed payload and the hex dump (`Transport/PacketLogFormatter.cs:37-78`); the HTTP login body is
  never read by the tracer.
- **Command authorization default.** New commands require an `Authenticated` session unless they
  opt out (`Dispatch/CommandAttribute.cs:33-39`), with a test pinning the pre-login allowlist.
- **Rate limiting** on registration, ranking and admin login, plus a shared socket login throttle.
- **Admin auth** as summarized in section 6.
- **CSRF on the admin surface.** Razor Pages validates the antiforgery token on every POST handler
  automatically; no page declares `[IgnoreAntiforgeryToken]` (test), and `/gameid_auth` stays
  reachable because it is a minimal API with no `[FromForm]` metadata. `UseAntiforgery()` is
  intentionally absent so it cannot block the game's token-less request.
- **Output encoding** on every page; no raw HTML helpers.
- **Secrets are not committed.** `certs/`, `docker/.env`, `conf/server.yaml`, logs, `data/` and the
  new admin password placeholders are ignored; the shipped admin password is empty (portal off).
- **Network layout.** MySQL is internal-only; only the game and HTTPS ports are published.

## 8. Remediation plan

| Order | Work | Why |
|---|---|---|
| 1 | M-2: replace the owner gate on event commands with a participant check; make scores idempotent; require `GAME` + both sides before settlement; participant/spectator matrix test | The current gate can drop legitimate away-side goals; `SET_INJURYGAMEEND` can settle outside a match |
| 2 | M-3: bind the roster watch to the caller's room, omit endpoints for non-participants, and confirm the friend/blacklist PID scope | Roster push is legitimate; scope and endpoint disclosure are not settled |
| 3 | A-4 residual: add `GameIdTicketStore.Revoke` and call it from the reset handler | A cached PES 2011 ticket outlives the reset by its TTL |
| 4 | L-1 ticket binding, L-4 non-root containers, L-6 text caps, L-7 delete/guard identity adoption, L-8 log defaults/headers | Defense in depth |
| 5 | A-4 (ops): `600` on `conf/server.<title>.yaml`; per-title passwords and `700` on `data/<title>` are already in place (5.1) | Deployment note, not code |
| 6 | CI: `dotnet list package --vulnerable --include-transitive`, fuzz tests for `KvReader`/`InnerBodyChunks`, and a participant/spectator matrix test | Prevent regressions |

## 9. Limitations and re-audit log

- Static review plus a build/test run at this revision: `dotnet test` passes (63 protocol tests,
  309 server tests, 0 failures). That covers the H-2/H-3/H-1 regressions, the A-2/A-3/A-4 fixes,
  the lobby list, the page-size allowlist, the reserved query-parameter rule and the stored-event
  parser. No exploit was executed. The participant/owner model in M-2 follows the maintainer's
  captures; the settlement gap in `CMD_SET_INJURYGAMEEND` is argued from handler code and
  `MatchResults.RecordAsync`. A capture, or a modified client, settles both.
- The roster/social scope in M-3 is deliberately left as "verify with a capture": the protocol
  notes say the roster subscription is legitimate for everyone in the room, and the emulator's
  handler cannot tell a member from an unrelated caller because it trusts the request field.
- A-1 was withdrawn after the maintainer ran the matrix on a live instance (token-less admin and
  register POSTs return 400; `/gameid_auth` returns 200) and added a test that no page opts out.
  That is stronger evidence than the static reading it replaces.
- Third-party package versions were not checked against vulnerability feeds.
- The admin portal, lobby editor, public site, live settings and `wwwroot/js/live.js` are
  **uncommitted** in the working tree as of this pass (`Pages/Admin/*`, `Web/AdminAuth.cs`,
  `Web/SiteData.cs`, `Configuration/LiveSettings.cs`, `LiveSettingCatalog.cs`, `Pages/*`,
  `wwwroot/js/*`). Review the final diff, commit, and re-run this audit against that commit so line
  references and behaviour match a reproducible revision.
- `main.py` and `tools/` remain untracked local reference material and were not reviewed; do not
  deploy them.
- **Third pass (2026-10-25):** reviewed the lobby editor and saved-lobby path, per-title admin
  credentials, the README security section, public match details/page sizes, and the live-refresh
  script. No new vulnerability was found; the only new item is the L-6 extension for event/lobby
  text. A-4 remains partially open for ticket revocation, and the per-title/password-file
  recommendations are now implemented or documented except the `conf` file mode.

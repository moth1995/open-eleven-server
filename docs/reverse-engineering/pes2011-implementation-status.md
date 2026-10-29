# PES 2011 (PC) — implementation status

Companion to [`pes2011-session-protocol.md`](pes2011-session-protocol.md), which holds the protocol
research. This file holds the state of the *implementation*: what runs, what is proven, and what is
still a guess. It is deliberately blunt — a build passing is not evidence of compatibility.

## Executable under analysis

| | |
|---|---|
| Path | `F:\Juegos\KONAMI\Pro Evolution Soccer 2011\pes2011.exe` |
| Size | 40,973,312 bytes |
| File version | 1.0.0.0 |
| Format | PE32, x86, little-endian, image base `0x00400000` |
| Ghidra language | `x86:LE:32:default`, 56,350 functions |
| Tooling | Ghidra 12.0.4 + GhidraMCP 5.7.1 (`127.0.0.1:8089`) |

Addresses below are absolute as Ghidra displays them for this image base. Anything re-derived on a
different build must be located by the referenced string or constant, not by address.

### Function and data addresses used

| Address | Role | Evidence |
|---|---|---|
| `0x01569084` | 4-byte XOR key `5B 9F 2E 64` | byte-pattern search; used by the frame reader |
| `0x0160c6b0` | 56-byte Blowfish key | byte-pattern search; consumed by `FUN_00feba00` |
| `FUN_00feba00` | Blowfish key schedule | `iVar4 % 0x38` over the key at `0x0160c6b0` |
| `FUN_01006b40` | Blowfish cipher object ctor | calls `FUN_00feba00` |
| `FUN_01006660` | **frame reader** — 24-byte header, MD5, XOR, 0x400 cap | decompiled in full |
| `FUN_01011540` | connection object that drives `FUN_01006660` | caller of the reader |
| `FUN_0050d880` | registers `NETCLIENT` as the first command | `FUN_0051fc00("NETCLIENT")` |
| `FUN_005228a0` | NETCLIENT handshake state machine | state 0/1/2 |
| `FUN_00541e80` / `FUN_0056c5d0` | name → message-class dispatch; writes `msg="<name>",` | `"msg"` at `0x012f363c` |
| `FUN_0100cd40` | command-name → message class table (~23 classes) | decompiled |
| `FUN_00512320` | builds `CMD_REG_SVRLIST` self-registration payload | gate host + immediate `0xfaa` |
| `FUN_00ffd160` | `CMD_GET_SVRLIST` **response parser** | `server_list[%d].%s` at `0x013ba1a4` |
| `0x013ba23c` | `svrtype` comparison table: `MENU`, `AUTOLOGIN`, `ACCOUNT`, `GATE`, `VDPCHAT` | string dump of `0x013ba1a0`–`0x013ba420` |
| `0x013ba3a0` | canned failure `result="%s",server_num=0,server_list=[]` | string dump |
| `FUN_0061c360` | login state machine (AUTH_GAMEID → LOGIN) | decompiled in earlier pass |
| `FUN_006425c0` | `CMD_LOGIN` sender | decompiled in earlier pass |
| `0x0130c9b8` | `account_first_login` response field | string search |
| `0x0130cc90`–`0x0130cdc0` | gate state machine's **literal table**: `timeout`, `svrtype="GATE"`, `lang`, `uri`, `read_buffer`, `logoutstat="FIN"`, `region` | memory dump |
| `0x013bee08`…`0x013bee90` | `ulist` entry `type` vocabulary (`PC_SPEC`, `KONAMIID`, `GAMEID_AUTH`, `EULA_PES`, `PATCH_DL`, …) | memory dump |
| `0x012e4a20` | `CMD_GET_URLLIST` response keys `url_num`, `ulist[`, `type`, `url`, `version`, `file_size`, `md5` | memory dump |

## PES 2010 vs PES 2011 compatibility matrix

| Component | PES 2010 | PES 2011 | Reuse |
|---|---|---|---|
| Gateway connection | TCP, `CMD_GET_SVRLIST` on a fixed gate port | TCP, same command, port **4010** (`0xfaa` in `FUN_00512320`) | **Shared transport, different port** (config) |
| Transport / ports | 24-byte frame, gate + 5 service ports | **identical 24-byte frame**; gate 4010, others from `server_list` | **Directly reusable** (`PacketCodec`) |
| Packet framing | `[u16 id][u16 len][u32 ctr][16B MD5]`, XOR `5B9F2E64` | **identical** (`FUN_01006660`) | **Directly reusable** (`XorCipher`, `PacketCodec`) |
| Packet body | `0x0060` → Blowfish-ECB, 56-byte key | **same key, same ids** | **Directly reusable** (`BlowfishEcb`) |
| Inner body | `[len][len][flag][ASCII KV]`, 8-byte padded | *assumed identical* — not re-derived | **Reused, INFERRED** (`InnerBody`) |
| Max frame | not checked in this repo | **1024 bytes**, enforced by the client reader | **New** (constant in `GameConnection`) |
| Authentication | `MSG_REQCCODE` + `MSG_REQAUTH` challenge/response | **different**: `CMD_AUTH_GAMEID` ticket, then `CMD_LOGIN` | **Separate implementation** |
| Encryption | XOR + Blowfish only | same; `PASS_AES` exists on `gameid_password` but its key is unrecovered | **Separate** (AES refused) |
| Server list | `server_num` + `svrlist`, entries `svrtype/svrname/svrport/svraddr/max_player_num/player_num/svrgid` | `server_num` + `server_list`, entries `svrtype/svraddr/svrport/userCount` only | **Separate payload** (`ServerCatalog` branches) |
| `svrtype` values | `GATE/FDLOBBY/LOBBY/MENU/ACCOUNT/VDPCHAT` | only `MENU/AUTOLOGIN/ACCOUNT/GATE/VDPCHAT`; anything else ⇒ `ERR_CLIENT_OPTFORMAT` | **Separate mapping** |
| Lobby system | `CMD_JOIN_LOBBY` / `CMD_JOIN_BLOCK` / block list | same command names documented, not yet implemented | **Not implemented** |
| Room management | implemented (`RoomHandlers`) | command names/fields documented (§6), not implemented | **Reverse-engineered, not implemented** |
| Match negotiation | implemented for 2010 | documented (§6/§8), not implemented | **Reverse-engineered, not implemented** |
| STUN / NAT | shared `pes7stun-*` service | **same hostnames**, port not a static constant | **Reusable in principle; unverified** |

The two rows to read carefully are *inner body* and *STUN*: they are the ones where "looks the same"
is doing work that evidence has not done.

## First real client session (2026-10-23)

A retail `pes2011.exe` was pointed at this server via `hosts`. It connected and produced this
exchange, which is the first evidence that does not come from a hand-written probe:

```
Gate  opened 127.0.0.1:52937
IN  CMD_GET_SVRLIST   rqid=1     OUT NOERR
IN  CMD_GET_SVRVERSION rqid=2    OUT NOERR
IN  CMD_GET_SVRTIME    rqid=3    OUT NOERR
IN  CMD_GET_URLLIST    rqid=4    OUT NOERR
Gate  closed 127.0.0.1:52937        ← then the whole sequence repeated twice more
```

What this establishes:

- The frame, the XOR/Blowfish layer and the KV envelope are **accepted by the real client** — it
  decoded five replies in a row and advanced to the next command each time. Before this run the
  reuse of the PES 2010 transport was an inference; it is now client-confirmed.
- `CMD_GET_SVRLIST`, `CMD_GET_SVRVERSION`, `CMD_GET_SVRTIME` and `CMD_GET_URLLIST` payloads are
  accepted (no `ERR_CLIENT_OPTFORMAT` abort, no disconnect at the reply).
- The client does **not** send `NETCLIENT` first on this path: it opens with `CMD_GET_SVRLIST` and
  uses `rqid=1..4` from a fresh counter. `NETCLIENT` remains implemented because `FUN_0050d880`
  registers it, but it was not observed here — worth re-checking on the ACCOUNT socket.
- The first real blocker is immediately after `CMD_GET_URLLIST`: the client closed the gate
  connection and retried the identical sequence three times, never fetching a single URL
  (no `HTTP GET` line in the trace) and never opening the ACCOUNT connection.

Diagnosis at the time: the `ulist` URLs carried an explicit `:8081`, and the client issues no
request at all. Changed to the shape the working PES 2010 setup uses — `AdvertiseIp: 127.0.0.1`
and HTTP on port 80, so the URLs become `http://127.0.0.1/pcspec.bin` with no port component.
This is a hypothesis under test, not a confirmed cause: the alternative is that the client rejects
another field of the `ulist` entry (`md5=0`, or `version=1`), and the retry loop alone cannot
distinguish them. The next client run decides it.

`pes2011pc.log` (see `Server:LogFile`) is required for this loop: the console output of a failing
attempt is gone before it can be read.

## Status

### Implemented and client-tested

Nothing has completed the *whole* online flow. What has been confirmed against a retail client is
the gate half: the client accepted the framing, decrypted five replies and advanced through
`CMD_GET_SVRLIST` → `CMD_GET_SVRVERSION` → `CMD_GET_SVRTIME` → `CMD_GET_URLLIST`, then stalled on
the step after `CMD_GET_URLLIST` (see "First real client session" above). Everything past that
point is still unproven with a real client.

What *has* been proven end to end is that the server speaks the recovered wire format: a separate
Python implementation of the frame (`tools/probe_pes2011.py`, using pycryptodome and its own
MD5/XOR/Blowfish code, sharing nothing with the C# server) connected to the running server and
completed the full sequence below. That rules out "the server only agrees with itself", and it is why
the framing is listed as verified rather than assumed.

### Implemented and tested (unit + live socket, no game client)

| Feature | Where | Evidence |
|---|---|---|
| Frame reuse for PES 2011 | `Protocol/Framing`, `Protocol/Crypto` | `FUN_01006660`; keys at `0x01569084` / `0x0160c6b0` |
| First command `NETCLIENT` | `Gate2011Handlers.NetClient` | `FUN_0050d880`, `FUN_005228a0` |
| Gate self-registration `CMD_REG_SVRLIST` | `Gate2011Handlers.RegisterServerList` | `FUN_00512320` |
| Server list in the PES 2011 shape | `GateHandlers.GetServerList`, `ServerCatalog` | `FUN_00ffd160`, `0x013ba1a4`, `0x013ba23c` |
| `svrtype` restricted to the five parsed values | `ServiceRoleExtensions.ToPes2011SvrType` | `0x013ba23c` |
| Login stage 1 `CMD_AUTH_GAMEID` (mints a ticket) | `Gate2011Handlers.AuthGameId`, `GameIdTicketStore` | `FUN_0061c360` state 2 |
| Login stage 2 `CMD_LOGIN` (redeems the ticket) | `Gate2011Handlers.Login` | `FUN_0061c360` state 7, `account_first_login` |
| Cached-ticket replay across connections | `GameIdTicketStore` (not single-use) | `FUN_0061c360` state 1 |
| Credential redaction in packet logs | `PacketLogFormatter.Redact` | local policy; verified by test + log inspection |
| `CMD_GET_URLLIST` needs **no** PES 2011 variant — the shared handler's `url_num`/`ulist` shape and its four `type` tokens are all in this binary's vocabulary | shared `GateHandlers` | `0x012e4a20`, `0x013bee08`–`0x013bee90` |
| `CMD_GET_SVRVERSION` needs **no** PES 2011 variant — `version`/`patch_version`/`dlc_version`/`eula_version` is exactly the literal run the client reads | shared `GateHandlers` | `0x012e49e8`–`0x012e4a0c` |
| 1024-byte outbound frame diagnostic | `GameConnection` | `FUN_01006660` |

Observed on the live socket (`tools/probe_pes2011.py`):

```
--> msg="NETCLIENT",rqid=1
<-- result="NOERR",msg="NETCLIENT",rqid=1
--> msg="CMD_GET_SVRLIST",rqid=2
<-- result="NOERR",msg="CMD_GET_SVRLIST",rqid=2,server_num=4,
    server_list=[{svrtype="GATE",svraddr="192.168.100.7",svrport=4010,userCount=0},
                 {svrtype="MENU",...},{svrtype="ACCOUNT",...},{svrtype="VDPCHAT",...}]
--> msg="CMD_AUTH_GAMEID",rqid=2,gameid="pes11probe",gameid_password="...",password_type="PASS_PLAIN"
<-- result="NOERR",msg="CMD_AUTH_GAMEID",rqid=2,loginid="…",loginid_password="…"
--> msg="CMD_LOGIN",rqid=3,svrtype="ACCOUNT",style="LOGINID",loginid="…",loginid_password="…"
<-- result="NOERR",msg="CMD_LOGIN",rqid=3,account_first_login="YES"
```

Failure paths observed on the same socket: wrong password → `ERR_AUTHGID_AUTH`; forged or expired
ticket → `ERR_LOGIN_FAILED`; unknown `style` → `ERR_LOGIN_FAILED`; mismatched serial with
`use_regcode="YES"` → `ERR_REGCODE_FAILED`.

### Implemented but unverified

- **`CMD_GET_HTTP`** returns `read_buffer=""`. The request field (`uri`, at `0x0130cce0`), the
  response field and the success envelope (`0x013bb5b4`) are verified; *what the original service
  returned for a given `uri`* is not. The handler logs the requested `uri` at Information so the first
  real client run names the resource to implement. An empty buffer is a placeholder and is labelled
  as such in the handler.
- **Inner body `[len][len][flag]`** for PES 2011 (see the matrix row). Reused from the validated PES
  2010 implementation on the strength of every surrounding byte matching.
- **`CMD_GET_PLAYERINFO` / `CMD_GET_PLAYERLIST` after login** are served by the shared PES 2010
  handlers. Both `player_count`/`player_data` and `player_num`/`player_list` literals exist in
  pes2011.exe, so the shared keys are plausible but not confirmed for this title.
- **Registration-code gate on `CMD_LOGIN`** uses verified field names (`use_regcode`, `regcode`) and
  verified error names, but the exact semantics of `use_regcode` are inferred.
- **`CMD_GET_SVRTIME`** answers with the shared handler's `date=<unix seconds>`. No `svrtime`, `time`
  or `date` literal exists as a defined string in pes2011.exe, so the key is unverified; the login
  state machine does not branch on the body, so an unused extra field is the low-risk choice.

### Reverse-engineered but not implemented

- Lobby/block selection, room create/join/leave, `CMD_WATCH_ROOMSTATE` push-driven readiness, and
  match negotiation (`CMD_GET_IPANDPORT`, `CMD_SEND_P2PFAILED`). Documented with field names in
  `pes2011-session-protocol.md` §5–§9. None of it is wired into `Pes2011Handlers` yet, and the
  shared PES 2010 room handlers must **not** be assumed compatible with it.
- `qmatchsetting` sub-object of `CMD_START_QUICKMATCH` (§7).

### Unknown or blocked

- **`PASS_AES` key** — `gameid_password` can arrive AES-wrapped; the key is not recovered, so the
  handler refuses that mode rather than treating ciphertext as a password.
- **STUN server port** — set at runtime through `mrdUPnP`, not a constant. Until it is known, P2P
  match negotiation cannot be completed for either title.
- **`AUTOLOGIN` / `VDPCHAT`** — accepted by the server-list parser, no code path found that uses them
  (`AUTOLOGIN` was searched exhaustively; the lead is dead).
- **Gate → ACCOUNT endpoint split** — confirmed to be a separate TCP connection by
  `FUN_0050cfd0("ACCOUNT")`, but whether the original ran it on a different host is not recoverable
  from the client.

## Running it

```bash
dotnet run --project src/OpenEleven.Pes2011Pc -- --config conf/server.pes2011pc.yaml
```

Gate listens on **4010**; `MENU`, `ACCOUNT` and `VDPCHAT` are advertised on the ports in
`conf/server.pes2011pc.yaml`. `FdLobby`/`Lobby` are configured but intentionally **not** advertised
for this title, because PES 2011's parser rejects their `svrtype` tokens.

Verify with the independent probe:

```bash
python tools/probe_pes2011.py --port 4010 NETCLIENT CMD_GET_SVRLIST
python tools/probe_pes2011.py --port 28014 --game-id <account> --password <password>
```

The probe needs an account: `POST http://127.0.0.1:8081/api/register` with
`{"gameId": "...", "passwordHash": "<md5-hex of the ASCII password>", "regCode": "..."}`.

## Tests

`tests/OpenEleven.Server.Tests/Pes2011Tests.cs` (20 tests) covers the server-list shape and its exact
wire text, the `svrtype` restriction, the empty-list failure envelope, `NETCLIENT`, `CMD_REG_SVRLIST`,
`CMD_GET_HTTP`, the full ticket login, cached-ticket replay, forged tickets, wrong passwords,
`PASS_AES` refusal, serial gating, unknown `style`, and the 1024-byte frame bound (round-tripped
through `PacketCodec`).

`tests/OpenEleven.Server.Tests/ProtocolSecretRedactionTests.cs` (13 tests) covers credential masking,
hex-dump withholding, nested records/lists, and false-positive checks.

`Pes2010_discovery_keeps_its_own_payload` guards the PES 2010 payload from the new branch.

## Next step

The shortest path to a *client-tested* result is the real client at the path above:

1. Map `pes11pcgate-e.winning-eleven.net` → `127.0.0.1` and `id.konami.net` → `127.0.0.1` in
   `hosts`, then start the server and register an account.
2. Launch the client, pick "Online", and watch the packet log. The gate sequence
   (`NETCLIENT`, `CMD_REG_SVRLIST`, `CMD_GET_SVRVERSION`, `CMD_GET_SVRLIST`, `CMD_GET_HTTP`,
   `CMD_GET_SVRTIME`, `CMD_GET_URLLIST`) is implemented; the first command that appears in the log
   without a matching `OUT` line is the next thing to research.
3. The likely first blocker is `CMD_GET_HTTP` (empty buffer) or the HTTP URLs `CMD_GET_URLLIST`
   advertises, since the PES 2010 flow depends on those being fetchable.

## Communities (`CommunityHandlers`)

Reply shapes read from `pes2011.exe`; the client accepts at most 5 joined communities and 32 members.

| Command | Reply |
|---|---|
| `CMD_CREATE_COMMU` | `commu_id` |
| `CMD_GET_JOINEDCOMMULIST` | `commu_num` (< 6) + `clist` (`FUN_005255a0`) |
| `CMD_SEARCH_COMMU` | `commu_num` (<= 100), `total_found_num`, `mlist` of community entries (`FUN_005260f0`) |
| `CMD_SEARCH_COMMU_BYRANKING` | `commu_num`, `mlist` of `{value, commu_info={entry}}` (`FUN_0054fe50`) |
| `CMD_JOIN_COMMU` | result only |
| `CMD_SET_CURRENT_COMMU` / `CMD_RESET_CURRENT_COMMU` | result only; the session keeps the current community, which settings, delete and the team/league lists act on |
| `CMD_GET_COMMU_INFO` | `commu_info` = one community entry (`FUN_00529290`) |
| `CMD_SET_COMMU_OPTION`, `CMD_DEL_COMMU`, `CMD_DEL_COMMU_MEMBER`, `CMD_WITHDRAW_COMMU` | result only; owner-only except withdraw (`ERR_NOTCOMMUOWNER`) |
| `CMD_CREATE_COMMU_LEAGUE` | `compe_id` (the client treats a missing one as failure); at most 3 open per community (`ERR_COMMUCOMPEISFULL`) |
| `CMD_GET_COMMU_LEAGUELIST` | `compe_num` (< 4) + `compe_list`: `compe_id`, `opening_date`, `entered_cell_count`, `finished_match_count`, `total_match_count`, `compe_chairman_info` (player block), `entry_info` (cells, see below), `env_setting` (`compe_name`, `comment` as text; `match_time` index, `injury`, `ball_type`, `home_and_away` as 16-bit numbers, not the names the request uses) (`FUN_00556e40`, `FUN_00554c70`) |
| `CMD_GET_COMMU_LEAGUEENTRYCELLINFO` | `cell_info_num` (<= 20) + `clist` of cells: `cell_id`, `team_id`, `entry_user_count`, `entry_user_info` (player blocks) (`FUN_0054b190`) |
| `CMD_ENTRY_COMMU_LEAGUECELL` | `joined_cell_id`; cell_id 0 opens a new cell (a guess: the client's request for a new cell has not been seen) |
| `CMD_LEAVE_COMMU_LEAGUE` | result only; sent when the player backs out of the league menu (client-confirmed), so it only closes the league on their side and keeps their cell and team |
| `CMD_DEL_COMMU_LEAGUEMEMBER`, `CMD_END_COMMU_LEAGUE` | result only; for the league's chairman or the community owner |
| `CMD_SET_COMMU_TEAM` / `CMD_GET_COMMU_TEAM` | the cell's `team_id` and `member` list (gameplayer_id, goal, assist, uniform_number, cards, injury_all/one/data) are stored as sent and played back with `cell_id` and `member_num` (`FUN_00526ed0`, `FUN_00529070`, `FUN_0054e2c0`) |
| `CMD_REQWATCHL_COMMU_COMPEMEMBERLIST` | the member list entry, for the league's members |
| `CMD_GET_COMMU_PASTLEAGUELIST` | `list_num` (<= 20) + `rlist`: `compe_id`, `compe_name`, `opening_date`, `finish_date`, `user_count`, `compe_chairman_info` (`FUN_005542d0`) |
| `CMD_GET_COMMU_TEAMLIST` | `list_num` (<= 400) + `team_list`, a plain list of team ids |
| `CMD_GET_COMMU_MEMBERRANKING` | `data_num` (<= 32), `date`, `ranking_list`: `disp_rank`, `value`, `member_info` (`FUN_005533f0`) |
| `CMD_GET_COMMU_TEAMRANKING` | `data_num` (<= 20), `date`, `ranking_list`: `team_id`, `disp_rank`, `value` (`FUN_00553c20`) |
| `CMD_GET_COMMU_MEMBERSRESULTS` / `TEAMRESULTS` | `stats` = one entry of totals plus `{total_count, average}` lists (`FUN_00555ff0`); worked out from the community's matches |
| `CMD_GET_COMMU_COMPERANKING` | `info_num` (<= 20), `date`, `i_list`: `team_rank_disp/point/win/draw/lose/score/lost/scorediff` + `entry_info` (a cell) (`FUN_0054d1c0`) |
| `CMD_GET_COMMU_PASTLEAGUERESULT` | `info_num` (<= 20) + `ilist`, the same entry, for a finished league |
| `CMD_GET_COMMU_COMPERESULT` | `info_num` (<= 380) + `mlist`: `played` (YES/NO), `home_id`, `home_pts`, `away_id`, `away_pts` (`FUN_0054dac0`) |
| `CMD_GET_COMMU_GOALRANKING` / `ASSISTRANKING` | `data_num` (<= 20), `date`, `goal_ranking` / `assist_ranking`: `disp_rank`, `ga_count`, `entry_info` (a cell) (`FUN_0054c990`) |
| `CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST` | `match_list` (<= 38) of `{match_id, home_team_info=[cell], away_team_info=[cell]}` (`FUN_0054b940`) |
| `CMD_REQWATCHL_COMMU_MATCHLIST` | `match_list` (<= 38) of `{match_status (a game phase: ENTRY, END...), match=[the entry above], home_goal=[score], away_goal=[score]}`; a score is `score_1st/2nd/ex1st/ex2nd/pk` (`FUN_005556b0`, `FUN_0054c1f0`) |
| `CMD_WATCH_COMMU_MATCH` | parked; a push of `compe_id`, `match_id`, `from_pid`, `room_id` (the other cell's offer, `FUN_005283e0`) |
| `CMD_WATCH_COMMU_MATCH_WAITING` | parked; pushes `player_num` (<= 2) + `player_list` (player blocks of those who said yes), or `ERR_COMMUMATCHREFUSED` / `ERR_COMMUMATCHCANCELED` |
| `CMD_APPLY_COMMU_MATCH`, `CMD_REPLY_COMMU_MATCH`, `CMD_CANCEL_COMMU_MATCH` | result only; apply puts a fixture up in the applicant's own room, reply is `YES` or anything else |
| `CMD_GET_COMMU_MEMBERSRESULTS_VS`, `CMD_GET_COMMU_TEAMRESULTS_VS` | not on the socket: the client GETs the `COMMU_MEMBERS_VS` / `COMMU_TEAM_VS` url with `commu_id=..&base_pid=..&oppo_pid=..` (or `base_team_id`, `oppo_team_id`) after it (`FUN_011be170`) and takes the body as the reply: `result="NOERR",stats=[...]` (`FUN_011be0f0`, `FUN_00552990`) |

League fixtures: every pair of cells plays, twice when the league is home and away. They are created as cells appear and dropped, if unplayed, when a cell goes. The offer (apply/reply/cancel) lives in memory; a room that played a fixture hands its score to the league when it ends. Whether the client sends a brand-new cell as `cell_id` 0, how it answers an offer and the exact strings of `reply` are still guesses.
| `CMD_REQWATCHL_COMMU_MEMBERLIST` | `list_type=ADD`, `member_list_num` (<= 32), `member_list` (`FUN_005257a0`, `FUN_00549fb0`) |

A member entry: `mode` (enum `NONE`/`NETWORK`/`COMPE`/`COMMUNITY`/...), `lobby_name`, `block_name`, `commu_name`, `commu_id`, `room_id`,
`beginnermark`, `position_want`, `selfreport_level`, `desired_position` and `info` (one player block, as `owner_info`).

A community entry (`FUN_0054e980`): `commu_id`, `member_count`, `logon_member_count`, `total_match_count`,
`total_legends_count`, `total_compe_count`, `update_date`, `start_time`, `end_time`, `commu_name`,
`commu_description`, `invite_only`, `password`, `week` (7 `YES`/`NO`, then an empty element) and
`owner_info` (one entry: `pid`, `name`, `manner`, `division`, `rating`, `status`, `lang`, `index`, `npid`).
`status` is enum `LOGIN`/`LOGOUT`/`COMMU_DELETED`. The client turns a non-empty `password` into "ask for a
password", so everyone but the owner is sent `*` for a protected community.

Unverified in game: the ranking entry nesting, the search page size (10), whether `start_time`/`end_time` are
`HOUR_*` enum positions, and invite-only handling (hidden from search, not joinable: no invitations yet).
Community match rooms: the client never joins a lobby, so `CMD_SET_CURRENT_COMMU` puts the session in a block of its own for that community (`Hub.CommunityBlockId`); its room list and rooms are the community's. Records and rankings come from `MatchRecord.CommuId` (set for rooms in a community block); fouls and the host's possession/shot figures are kept as `FOUL` and `MATCHINFO` event lines. Averages are whole numbers and their scale in the client is unverified. The head-to-head urls are advertised in the url list. The member list is a snapshot, not pushed.

### Client-tested community fixes (2026-10-28)

- Member list entries carry `info.index`: the client files each entry in a 32-slot array under it
  (`FUN_00543d70`), so every entry needs its own index (all zeros broke joining as a member).
- Member list `mode` is where the member is: `COMMUNITY` inside the open community, `COMMUNITY_COMPE`
  (plus `compe_id`) inside the open league, `NETWORK` online elsewhere, `NONE` offline. The
  "Membership Numbers" and "Number of Participants" counts are built from it. The community one is
  client-confirmed (1/2); the league one (COMMUNITY_COMPE) is not yet.
- `CMD_GET_GAMEPLAN` / `CMD_SET_GAMEPLAN` (`AccountHandlers`): the hex block is stored verbatim in
  `Players.GamePlan`; nothing saved is `ERR_DATANOTFOUND`. Not yet checked in the client.
- Chat channel `COMMUNITY_LEAGUE` reaches the participants of the league the sender has open.
- The community and league member lists are watches (`CMD_REQWATCHL_COMMU_MEMBERLIST`,
  `CMD_REQWATCHL_COMMU_COMPEMEMBERLIST`): the server keeps the rqid and pushes `list_type` ADD/UPDATE
  (upsert by `info.pid`) or DEL deltas when a member joins, withdraws, is removed, opens or closes the
  community, or joins, leaves or is removed from the league. A connection that just drops is not
  pushed yet. The league's own counts (`entered_cell_count`, `total_match_count`) come from
  `CMD_GET_COMMU_LEAGUELIST`, which is a plain request: they refresh when the screen is reopened.
- A league cell holds exactly one player (`FUN_0054b190` reads a single player block per cell); the
  client always asks for `cell_id` 0 and the server opens the cell.
- **Starting league matches needs 3 participants (client rule).** The community menu builder
  (`FUN_005cb7b0`) enables the league-match item from `FUN_005cb430`, which returns false unless the
  league's member list (`CMD_REQWATCHL_COMMU_COMPEMEMBERLIST`, count at screen+`0xf6f0`) has at
  least 3 entries (`FUN_00523860() < 3`). After that it needs a playable match: `total_match_count`
  0 requires a non-empty `CMD_REQWATCHL_COMMU_PLAYABLEMATCHLIST`, otherwise `finished_match_count`
  must differ from `total_match_count`. With two players the option never appears.
- **The `CMD_WATCH_*` messages are never sent by the PES 2011 client** (not `CMD_WATCH_TEXTCHAT`,
  `CMD_WATCH_COMMU_MATCH`, nor `CMD_WATCH_COMMU_MATCH_WAITING`; none appears in a full two-player
  league session). The client only registers a handler by message name, so the server pushes them
  unsolicited (rqid 1 when nothing is parked). A league challenge goes: `CMD_APPLY_COMMU_MATCH`
  (after the client's own `CMD_CREATEJOIN_ROOM`), the offer is pushed as `CMD_WATCH_COMMU_MATCH`,
  the opponent answers `CMD_JOIN_ROOM` + `CMD_REPLY_COMMU_MATCH`, and both players' "Matchmaking in
  progress" screens are filled by a `CMD_WATCH_COMMU_MATCH_WAITING` list (`player_num`,
  `player_list`: the applicant, then each player who said yes). What starts the match after that is
  not yet known.
- `CMD_GET_COMMU_TEAM` plays back the client's saved squad with each member's `goal`, `assist`,
  `total_yellow_card` and `total_red_card` worked out from the events of the league's finished
  matches (by the cell's side in each match and the event's `gameplayer_id`; the scorer's pid is 0
  for some goals, so it is not used). `now_*_card_count` stay as the client saved them.
- `CMD_SET_COMMU_MATCHINFO` (possession in hundredths, shots) arrives about a second after the end
  phase has saved the match, so it is appended to the saved record's events
  (`MatchResults.AppendEventAsync`); only the host's report is kept.

## Competitions (KONAMI CUP ONLINE) — client requirements found

- `CMD_JOIN_COMPLG` NOERR must carry `is_select_team` (YES/NO, `FUN_0055f140`); without it the client treats the join as failed and stays on the detail page. Sent NO (no team is kept per entry).
- Qualifying round screens: `CMD_REQWATCH_COMPLG_PLAYERNUM` → `player_num` only (`FUN_00560500`); `CMD_GET_COMPLG_MATCH_START_TIME` → `is_waiting_time` TRUE|FALSE (+ `match_start_time` when TRUE, `FUN_00561180`).
- Rankings (`PRE_RESULTS`, `GOALRANKING`, `ASSISTRANKING`) open with `rank_num`, `start_rank`, `rank_count`, `rank_list`; `PRE_RESULTS` also needs `compe_name` and `date`. They answer empty: no competition match is played or recorded yet.
- `CMD_GET_COMPLG_MYRESULTS` needs `team_id`, a non-empty `team_name`, `my_rank`, `match_result_num` (<= 20) and `match_result_list`. `CMD_SET_COMPLG_TEAM` (team_id, team_name, member_num, member[]) is kept on the player and played back by `CMD_GET_COMPLG_TEAM_DATA` (`ERR_COMPE_NOT_SELECT_TEAM` until one is picked); the popularity ranking counts the picks. The client ends the member list with a comma (`member=[{..},{..},]`), which `KvReader` accepts.
- Still not handled (no competition match flow yet): `CMD_GET_COMPLG_TOURNAMENT`, `PLAYOFF_MATCH`, `PLAYOFF_PLAY_OK`, `PLAYOFF_RESULTS`, `COMMENDATION_DATA`, `DIGESTLIST`, `REQWATCHL_COMPLG_MATCHLIST`, `QUIT_COMPLG`, `CREATE_COMPLG`, `ADD_COMPLG_RESULTS`, and the pushes `WATCH_COMPLG_END`, `END_MATCH`, `GAMEDATA`, `MATCHNOTICE`, `MATCH_START`, `MATCH_START_TIME`.

### Competition fields (enums from the master tables)

- `compe_kind` (enum 0x0a, 80 names): UEFA_Champions_League, International_Club_Championship, International_Cup_Online, KONAMI_CHAMPIONSHIP, **KONAMI_CUP_ONLINE**, WEPES_CLUB_OPEN, WEPES_NATIONAL_OPEN, WEPES_CHAMPIONSHIP, D1_Cup..D5_Cup, ML_* (13..25), RESERVED_CUP01..54. A name outside the table makes the client drop the whole reply. Normal lists always ask for KONAMI_CUP_ONLINE; the kind only filters `OLD_ALL` (past competitions).
- `category` (enum 0x08): PLATINUM, GOLD, SILVER, BRONZE. Per-competition data kept by the client; the list request has no category field, so it filters nothing.
- `entry_limit` (enum 0x09): WEEKRANK (uses `entry_limit_rank`) or DIVISION (uses the six `entry_limit_division` TRUE/FALSE flags, D5..D1 and NONE). `status` (enum 0x0e): NOCREATE, CREATED, PERMITSTART, ENTRY, STARTED, ENTRY_CLOSED, MATCH_CLOSED, FINISHED, CANSELED, END, CLEARED, DELETED.
- `player_capacity`, `player_num`, `fee`, `guest_count` are plain unsigned numbers; no client-side upper bound was found in the list parser (`FUN_0055cdb0`). Other detail keys: `champion_notice`, `is_coach_mode`, `need_konamiid`, `use_national`/`use_club`/`use_ml`, `gametime` (3 entries: preliminary, playoff, final), `usable_ball_pre/playoff/final_num`, optional `banner_file_name` (<= 15), `ad_file_name`, `get_ball_id`, `get_spike_id`.
- Qualifying round screen menu: a carousel that starts at the item last used, so earlier items are scrolled out of view (exit last -> only the exit icon). Not server state.
- "To Next Match" sends `MSG_QUICK_REQ_MATCHING_START` with `type="COMPE"`; the quick-match queue pairs it like any other type for now.
- `CMD_GET_COMPLG_TEAM_DATA` (`FUN_005640f0`) drops the reply unless it has top-level `pid` and `name` as well as `team_id`, `team_name`, `member_num`, `member`; without them the competition match aborts right after the squad arrives.
- A "To Next Match" room carries `Room.CompeId` (both entrants' competition), copied to `MatchRecord.CompeId`. Qualifying round data is worked out from those records: standings (`CMD_GET_COMPLG_PRE_RESULTS`, 3 points a win, 1 a draw), goal/assist rankings, `CMD_GET_COMPLG_MYRESULTS` (match_result_list entry = date + home/away sides: pid, manner, rating, team_id, name, division, is_guest_player TRUE|FALSE, npid, team_name, score{score_1st,score_2nd,score_ex1st,score_ex2nd,score_pk}) and the per-footballer goal/assist/cards laid over `CMD_GET_COMPLG_TEAM_DATA` members. Matches played before `CompeId` existed have 0 and do not count.

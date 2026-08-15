# pes2010.exe — Online `CMD_` Command Inventory (Pass 1)

Source binary: `F:\Juegos\KONAMI\Pro Evolution Soccer 2010\pes2010.exe` (x86, analyzed live via Ghidra MCP, image base `00400000`).

This is **pass 1** of the online-protocol command survey: every `CMD_` string in the binary, whether it is a
client **input** (request awaiting a direct server response) or a server-pushed **output** (asynchronous
"watch" notification), and which code is responsible for building/parsing it. A later pass will add the
concrete field/parameter layout for each command.

## How this was derived

The client keeps every command name as a C string and routes it through one master factory function,
**`FUN_00bc7fb0`** (`CCmdFactory::Create`-style dispatcher). It string-compares the incoming command name
against ~32 hard-coded cases; anything not hard-coded falls through to one of two **generic** command
classes:

- **`CMD_COMMON_SENDRECV`** → generic request/response class, constructed by `FUN_01085d00`
  (vtable `PTR_FUN_0121f4a4`, base ctor `FUN_00bc3d10`). Matched against a name table of **145** commands
  living at `012a5220` (array of `char*`, count at `012a54e4+8` = `0x91`).
- **`CMD_COMMON_WATCH`** → generic async/"watch" class, constructed by `FUN_0106afc0`
  (vtable `PTR_FUN_0121f1a4`, base ctor `FUN_00baf420`). Matched against a name table of **30** commands
  living at `012a5468` (count at `012a54f0+8` = `0x1e`).

Both generic classes resolve their actual wire-format (field list) at construction time from a shared
format-catalog lookup (`FUN_00b6e6f0` / `FUN_00b6ee70` against resource blobs `DAT_01181478` /
`DAT_01184388`), keyed by the numeric command ID assigned to the name — not by a dedicated per-command
function. That per-field catalog is what pass 2 will decode.

The same two group names are also used by a **packet-logging classifier** (`FUN_0077e400`, called from
`FUN_00768e50`) that walks the identical 145/30 name tables to label debug output as
`CMD_COMMON_SENDRECV` or `CMD_COMMON_WATCH` — independent confirmation of the grouping.

**Direction rule used below:**
- **Input** = client-initiated request that expects a direct, synchronous server response (send → recv pair).
- **Output** = server-initiated/asynchronous push that the client "watches" for (`CMD_WATCH_*` family,
  plus a handful of bespoke list-watchers); there is no 1:1 client request tied to each message.

---

## 1. Bespoke commands (own dedicated handler class)

These 26 real `CMD_` names are special-cased directly inside `FUN_00bc7fb0` instead of going through the
generic engine, meaning each has its own constructor/class implementing both packet-build and
response-parse. Address given is the **constructor** Ghidra picked up as the entry point for that class;
the class vtable (send/parse methods) hangs off `*param_1` inside each constructor.

(Not listed: 6 non-`CMD_` pseudo-types also special-cased in the same function — `CONNECT`, `SENDECHO`,
`SENDSTRING`, `SENDTEXT`, `KEEPLASTRCV`, `NETCLIENT` — internal local queue markers, out of scope per your
"only `CMD_`" instruction.)

| Command | Direction | Constructor (handler) | Notes |
|---|---|---|---|
| `CMD_AUTH_GAMEID` | Input | `FUN_00bc4350` | Game/platform ID authentication |
| `CMD_CHECK_STUN` | Input | `FUN_00bc2bf0` | NAT/STUN connectivity check |
| `CMD_COMMON_SENDRECV` | Input (meta) | `FUN_01085d00` | Not a real command — generic request/response engine (see §2) |
| `CMD_COMMON_WATCH` | Output (meta) | `FUN_0106afc0` | Not a real command — generic watch engine (see §3) |
| `CMD_DISCONNECT` | Input | `FUN_00bc30e0` | Graceful disconnect |
| `CMD_DOWNLOAD_AD` | Input | `FUN_01085f70` | In-game advertisement download |
| `CMD_GET_EULA` | Input | `FUN_01086360` | Fetch EULA text |
| `CMD_GET_MYPLATFORMID` | Input | inline in `FUN_00bc7fb0` (vtable `PTR_FUN_0121f158`) | No separate ctor function — object built inline |
| `CMD_GET_RANKING` | Input | `FUN_01086b70` | Leaderboard/ranking query |
| `CMD_GET_COMMU_MEMBERSRESULTS_VS` | Input | `FUN_01086f00` | Head-to-head community member results (bespoke variant of the generic `CMD_GET_COMMU_MEMBERSRESULTS`) |
| `CMD_GET_COMMU_TEAMRESULTS_VS` | Input | `FUN_01086610` | Head-to-head community team results (bespoke variant of generic `CMD_GET_COMMU_TEAMRESULTS`) |
| `CMD_LEAVE_QUICKMATCH` | Input | `thunk_FUN_0045bc40` | Leave quick-match queue |
| `CMD_LOGIN` | Input | `FUN_00bbc7e0` | Account login |
| `CMD_REG_SVRLIST` | Input | `FUN_00bba860` | Register/refresh server list |
| `CMD_SEND_PCSPEC` | Input | `FUN_01087420` | Report client PC hardware spec |
| `CMD_SEND_STRING` | Input | `FUN_00bbb2e0` | Generic string upload (distinct from generic `CMD_CHECK_STRING`) |
| `CMD_SET_DUMMYSVR_REPLY` | Input | `FUN_00bb1830` | Dummy/test server reply hook |
| `CMD_START_QUICKMATCH` | Input | `FUN_00bc0ea0` | Begin quick-match search |
| `CMD_CONNECT_SVR` | Input | `FUN_00bb37a0` | Connect to a (sub-)server |
| `CMD_WATCH_BLOCK_PLAYERLIST` | Output | `FUN_0106b5d0` | Watches lobby-block player list |
| `CMD_WATCH_ROOMLIST` | Output | `FUN_00bb96b0` | Watches room list |
| `CMD_WATCH_ROOMPLAYERLIST` | Output | `FUN_00bb4060` | Watches room's player list |
| `CMD_WATCH_COMMU_MATCHLIST` | Output | `FUN_00bb7510` | Watches community match list |
| `CMD_WATCH_COMMU_MEMBERLIST` | Output | `FUN_00bb2810` | Watches community member list |
| `CMD_WATCH_COMMU_PLAYABLEMATCHLIST` | Output | `FUN_00bb6310` | Watches community playable-match list |
| `CMD_WATCH_COMPLG_MATCHLIST` | Output | `FUN_00bb85d0` | Watches competition-league match list |

---

## 2. Generic Send/Recv commands — Input (145)

All 145 share the same handler: constructor **`FUN_01085d00`**, base ctor `FUN_00bc3d10`, vtable
`PTR_FUN_0121f4a4`. Each is a synchronous client request with a matching server response; the concrete
field layout is resolved at runtime from the format catalog (pass 2 target), not from a per-command
function.

| # | Command | # | Command | # | Command |
|---|---|---|---|---|---|
| 1 | `CMD_ADD_SCORE` | 50 | `CMD_DEL_FRIEND` | 99 | `CMD_REPLY_COMMU_MATCH` |
| 2 | `CMD_CANCEL_STARTQUICKMATCH` | 51 | `CMD_DEL_FRIENDREQ` | 100 | `CMD_END_COMMU_LEAGUE` |
| 3 | `CMD_CANCEL_DECIDE_QUICKMATCH` | 52 | `CMD_DEL_INVITATION` | 101 | `CMD_SET_COMMU_OPTION` |
| 4 | `CMD_CHANGE_GAMEPHASE` | 53 | `CMD_DEL_SENDSHORTMAIL` | 102 | `CMD_SET_COMMU_TEAM` |
| 5 | `CMD_CHANGE_ROOMNAME` | 54 | `CMD_DEL_SHORTMAIL` | 103 | `CMD_GET_PLAYERNUMBERS` |
| 6 | `CMD_CHECK_STRING` | 55 | `CMD_GET_BLACKLIST` | 104 | `CMD_GET_QUICKMATCHNUMBERS` |
| 7 | `CMD_CREATE_PLAYER` | 56 | `CMD_GET_FRIENDLIST` | 105 | `CMD_SET_FDPROFILE` |
| 8 | `CMD_CREATEJOIN_ROOM` | 57 | `CMD_GET_FRIENDREQ` | 106 | `CMD_UPDATE_ROOMSTATE` |
| 9 | `CMD_DEL_BLACKLIST` | 58 | `CMD_GET_INVITATIONLIST` | 107 | `CMD_SET_COMPLG_GAMEDATA` |
| 10 | `CMD_DEL_PLAYER` | 59 | `CMD_GET_INVITEHISTORY` | 108 | `CMD_START_COMPLG_WATCHING` |
| 11 | `CMD_ENTRY_GAME` | 60 | `CMD_GET_SENDSHORTMAIL` | 109 | `CMD_SEND_ROOMP2PDATA` |
| 12 | `CMD_GET_BLOCKLIST` | 61 | `CMD_GET_SENDSHORTMAILLIST` | 110 | `CMD_SEND_ROOMP2PDATABYPID` |
| 13 | `CMD_GET_DIVISIONUPDATE` | 62 | `CMD_GET_SHORTMAIL` | 111 | `CMD_SET_CHATENABLE` |
| 14 | `CMD_GET_FD_GAMERESULTS` | 63 | `CMD_GET_SHORTMAILLIST` | 112 | `CMD_SET_GAMEEND` |
| 15 | `CMD_GET_GAMEADDPOINT` | 64 | `CMD_REPLY_FRIENDREQ` | 113 | `CMD_SET_XNADDR` |
| 16 | `CMD_GET_GAMERESULTS` | 65 | `CMD_SEND_FRIENDREQ` | 114 | `CMD_SET_XUID` |
| 17 | `CMD_GET_INFORMATIONLIST` | 66 | `CMD_SEND_INVITATION` | 115 | `CMD_UPDATE_INGAMETIME` |
| 18 | `CMD_GET_IPANDPORT` | 67 | `CMD_SEND_SHORTMAIL` | 116 | `CMD_AUTH_NPTICKET` |
| 19 | `CMD_GET_PLAYERINFO` | 68 | `CMD_CANCEL_COMMU_MATCH` | 117 | `CMD_ADD_FOUL` |
| 20 | `CMD_GET_PLAYERLIST` | 69 | `CMD_GET_COMMU_INFO` | 118 | `CMD_GET_COMPLG_PLAYOFF_RESULTS` |
| 21 | `CMD_GET_SVRLIST` | 70 | `CMD_GET_COMMU_LEAGUEENTRYCELLINFO` | 119 | `CMD_END_COMPLG_WATCHING` |
| 22 | `CMD_GET_SVRTIME` | 71 | `CMD_GET_COMMU_MEMBERSRESULTS` | 120 | `CMD_CREATE_COMPLG` |
| 23 | `CMD_GET_SVRVERSION` | 72 | `CMD_GET_COMMU_PASTLEAGUELIST` | 121 | `CMD_GET_COMPLG_MYENTRY_COMPEID` |
| 24 | `CMD_GET_URLLIST` | 73 | `CMD_GET_COMMU_PASTLEAGUERESULT` | 122 | `CMD_GET_COMPLG_INFO` |
| 25 | `CMD_JOIN_BLOCK` | 74 | `CMD_WITHDRAW_COMMU` | 123 | `CMD_GET_COMPLG_LIST` |
| 26 | `CMD_JOIN_ROOM` | 75 | `CMD_DEL_COMMU_MEMBER` | 124 | `CMD_JOIN_COMPLG` |
| 27 | `CMD_KICK_ROOMMEMBER` | 76 | `CMD_DEL_COMMU_LEAGUEMEMBER` | 125 | `CMD_SET_COMPLG_TEAM` |
| 28 | `CMD_LEAVE_ROOM` | 77 | `CMD_CREATE_COMMU` | 126 | `CMD_GET_COMPLG_MYRESULTS` |
| 29 | `CMD_REC_STATISTIC` | 78 | `CMD_JOIN_COMMU` | 127 | `CMD_GET_COMPLG_COMMENDATION_DATA` |
| 30 | `CMD_REQ_FORFEITEDGAME_REPLY` | 79 | `CMD_JOIN_COMMU_LEAGUE` | 128 | `CMD_GET_COMPLG_ASSISTRANKING` |
| 31 | `CMD_SEARCH_PLAYER` | 80 | `CMD_SEARCH_COMMU` | 129 | `CMD_GET_COMPLG_GOALRANKING` |
| 32 | `CMD_SEND_ABNORMALEND` | 81 | `CMD_ENTRY_COMMU_LEAGUECELL` | 130 | `CMD_LEAVE_COMPLG` |
| 33 | `CMD_SEND_DISCON_PLAYER` | 82 | `CMD_APPLY_COMMU_MATCH` | 131 | `CMD_SET_GUEST` |
| 34 | `CMD_SEND_DISCON_SETPLAYER` | 83 | `CMD_GET_COMMU_ASSISTRANKING` | 132 | `CMD_GET_COMMU_TEAM` |
| 35 | `CMD_SEND_HEARTBEAT` | 84 | `CMD_GET_COMMU_COMPERANKING` | 133 | `CMD_DEL_COMMU` |
| 36 | `CMD_SEND_OFFEROWNER` | 85 | `CMD_GET_COMMU_COMPERESULT` | 134 | `CMD_GET_WEEKLYRANKINGDATELIST` |
| 37 | `CMD_SEND_TEXTCHAT` | 86 | `CMD_GET_COMMU_GAMEINFO` | 135 | `CMD_SET_XSESSION` |
| 38 | `CMD_SET_CURRENTPLAYER` | 87 | `CMD_RESET_CURRENT_COMMU` | 136 | `CMD_SET_COMMU_MATCHINFO` |
| 39 | `CMD_SET_FORFEITEDGAMEEND` | 88 | `CMD_SET_CURRENT_COMMU` | 137 | `CMD_QUIT_COMPLG` |
| 40 | `CMD_SET_GAMEENV` | 89 | `CMD_GET_COMMU_COMPETEAMINFO` | 138 | `CMD_GET_COMPLG_KIND_LIST` |
| 41 | `CMD_SET_GAMEMEMBER` | 90 | `CMD_GET_COMMU_TEAMLIST` | 139 | `CMD_GET_COMPLG_PLAYOFF_MATCH` |
| 42 | `CMD_SET_GAMEMEMBERENV` | 91 | `CMD_GET_COMMU_TEAMRANKING` | 140 | `CMD_GET_COMPLG_PLAYOFF_PLAY_OK` |
| 43 | `CMD_SET_GAMETEAM` | 92 | `CMD_GET_COMMU_GOALRANKING` | 141 | `CMD_GET_COMPLG_PRE_RESULTS` |
| 44 | `CMD_SET_INJURYGAMEEND` | 93 | `CMD_CREATE_COMMU_LEAGUE` | 142 | `CMD_GET_COMPLG_TEAM_DATA` |
| 45 | `CMD_SET_LANGUAGE` | 94 | `CMD_GET_COMMU_LEAGUELIST` | 143 | `CMD_GET_COMPLG_TITLEHOLDER` |
| 46 | `CMD_SET_PLAYERPROFILE` | 95 | `CMD_GET_COMMU_MEMBERRANKING` | 144 | `CMD_GET_COMPLG_TOURNAMENT` |
| 47 | `CMD_UPDATE_COMBINATION` | 96 | `CMD_LEAVE_COMMU_LEAGUE` | 145 | `CMD_SET_COMPLG_GAMEDATA` *(dup, see note)* |
| 48 | `CMD_GET_PRIVATEINFO` | 97 | `CMD_GET_COMMU_TEAMRESULTS` | | |
| 49 | `CMD_ADD_BLACKLIST` | 98 | `CMD_GET_JOINEDCOMMULIST` | | |

> Note: `CMD_SET_COMPLG_GAMEDATA` is registered twice in the name table (slots 107 and 145, two distinct
> string literals at `01154708` and `01154a94`) — almost certainly a duplicate literal from two call sites
> rather than two different commands. Treat it as **144 unique names / 145 table slots**.

---

## 3. Generic Watch commands — Output (30)

All 30 share the same handler: constructor **`FUN_0106afc0`**, base ctor `FUN_00baf420`, vtable
`PTR_FUN_0121f1a4`. These are asynchronous, server-pushed notifications; the client registers a watch once
and then receives updates without sending a matching request per message.

| # | Command | # | Command |
|---|---|---|---|
| 1 | `CMD_WATCH_ABNORMALEND` | 16 | `CMD_WATCH_DISCON_PLAYERMATCH` |
| 2 | `CMD_WATCH_DECIDE_GAMEENV` | 17 | `CMD_WATCH_ENTRY_GAME` |
| 3 | `CMD_WATCH_DECIDE_GAMEPLAYER` | 18 | `CMD_WATCH_FORFEITEDGAMEEND` |
| 4 | `CMD_WATCH_DECIDE_GAMEPLAYERENV` | 19 | `CMD_WATCH_FRIENDLIST` |
| 5 | `CMD_WATCH_DISCON_PLAYERENV` | 20 | `CMD_WATCH_INJURYGAMEEND` |
| 6 | `CMD_WATCH_EMERGENCY` | 21 | `CMD_WATCH_KICKENTRYMEMBER` |
| 7 | `CMD_WATCH_ROOMSTATE` | 22 | `CMD_WATCH_OWNGOALGAMEEND` |
| 8 | `CMD_WATCH_FRIENDREQ` | 23 | `CMD_WATCH_UPDATE_GAMERECORD` |
| 9 | `CMD_WATCH_INVITATION` | 24 | `CMD_WATCH_COMPLG_END` |
| 10 | `CMD_WATCH_SHORTMAIL` | 25 | `CMD_WATCH_COMPLG_MATCH_START_TIME` |
| 11 | `CMD_WATCH_COMMU_MATCH` | 26 | `CMD_WATCH_COMPLG_MATCH_START` |
| 12 | `CMD_WATCH_COMMU_MATCH_WAITING` | 27 | `CMD_WATCH_COMPLG_GAMEDATA` |
| 13 | `CMD_WATCH_TEXTCHAT` | 28 | `CMD_WATCH_IPANDPORT` |
| 14 | `CMD_WATCH_MAINTETIME` | 29 | `CMD_WATCH_COMPLG_END_MATCH` |
| 15 | `CMD_WATCH_ALLREADYTOGAME` | 30 | `CMD_WATCH_COMPLG_MATCHNOTICE` |

---

## 4. Orphaned strings (present in binary, not wired to any live dispatcher)

Found via string search but **zero cross-references** anywhere in the program — not reachable from
`FUN_00bc7fb0` or either generic name table. Most likely leftovers from an earlier game revision's
protocol (dead code / unused literals). Flagged here rather than silently dropped, in case a second
dispatcher elsewhere in the binary still turns out to reference them.

| Command | Address | Notes |
|---|---|---|
| `CMD_GET_INVITATION` | `01146960` | Singular form; only `CMD_GET_INVITATIONLIST` (generic #58) is live |
| `CMD_ADD_COMPLG_RESULTS` | `01145918` | No live counterpart found in the CompLG (competition league) command set |

---

## Summary

| Group | Count | Direction | Handler |
|---|---|---|---|
| Bespoke (own class) | 26 | 24 Input / 6 Output-ish list-watchers — see §1 for exact split | Individual `FUN_*` constructors |
| Generic Send/Recv | 145 (144 unique) | Input | `FUN_01085d00` (shared) |
| Generic Watch | 30 | Output | `FUN_0106afc0` (shared) |
| Orphaned/unused | 2 | — | none (dead strings) |
| **Total live commands** | **201** (200 unique) | | |

## Pass 2

Done — see [`pes2010_cmd_fields.md`](./pes2010_cmd_fields.md) for the per-command key/field catalog,
connection (`svrtype`) mapping, and enum catalog.

## Original pass-2 plan (superseded by the file above)

For each command, decode the concrete request/response field list. For the 26 bespoke commands this means
decompiling each constructor's vtable methods individually. For the 175 generic commands, it means reverse
engineering the shared format catalog (`FUN_00b6e6f0` / `FUN_00b6ee70` against `DAT_01181478` /
`DAT_01184388`, keyed by the numeric command ID assigned in the base ctors `FUN_00bc3d10` /
`FUN_00baf420`) so field lists don't have to be pulled one command at a time.

using TenServer.Protocol.Kv;
using TenServer.Server.Configuration;
using TenServer.Server.State;

namespace TenServer.Server.Tests;

public class ServerListTests
{
    [Fact]
    public async Task Advertises_one_port_per_service_with_the_gate_pinned()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");
        var list = replies[0].GetList("svrlist");

        Assert.Equal(6, replies[0].GetInt32("server_num"));
        Assert.Equal(6, list.Count);

        Assert.Equal(28010, list.Single(e => e.GetString("svrtype") == "GATE").GetInt32("svrport"));

        var ports = list.Select(e => e.GetInt32("svrport")).ToArray();
        Assert.Equal(ports.Length, ports.Distinct().Count());

        // svrgid keys the client's internal service table, so it must stay stable.
        Assert.Equal([1, 2, 3, 4, 5, 6], list.Select(e => e.GetInt32("svrgid")).ToArray());
    }

    [Fact]
    public async Task Every_entry_advertises_the_configured_address()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.All(
            replies[0].GetList("svrlist"),
            entry => Assert.Equal("127.0.0.1", entry.GetString("svraddr")));
    }

    [Fact]
    public async Task Service_role_declarations_are_enforced_when_switched_on()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
            s["Server:Protocol:EnforceServiceRoles"] = "true");
        var session = harness.NewSession(ServiceRole.Lobby);

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.Equal("ERR", replies[0].GetString("result"));
        Assert.Equal("SVRTYPE", replies[0].GetString("reason"));
    }

    [Fact]
    public async Task Service_role_declarations_are_advisory_by_default()
    {
        // The reference server answered every command on one port, so a command that
        // turns up somewhere unexpected is logged, not refused, until traffic proves
        // where it belongs.
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby);

        var replies = await harness.DispatchAsync(session, "CMD_GET_SVRLIST");

        Assert.Equal("NOERR", replies[0].GetString("result"));
    }
}

public class InformationListTests
{
    [Fact]
    public async Task Information_list_is_served_from_the_database()
    {
        // Regression: ordering this query by a DateTimeOffset column is not translatable
        // on SQLite, which turned the whole command into an ERR/SERVER reply.
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_GET_INFORMATIONLIST");

        Assert.Equal("NOERR", replies[0].GetString("result"));
        Assert.Equal(1, replies[0].GetInt32("info_num"));
        Assert.Equal("Test", replies[0].GetList("ilist")[0].GetString("mes_subject"));
    }
}

public class PlayerProfileTests
{
    [Fact]
    public async Task Profile_edits_are_persisted_and_read_back()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        var replies = await harness.DispatchAsync(session, "CMD_SET_PLAYERPROFILE", withFields: r =>
            r.Set("profile", new KvMessage()
                .Set("date", 0)
                .Set("birthmonth", 3)
                .Set("birthday", 21)
                .Set("country", 7)
                .Set("area", 2)
                .Set("favorite_team", 11)
                .Set("favorite_player", 1234)
                .Set("intro", "hello there")));

        Assert.Equal("NOERR", replies[0].GetString("result"));

        var info = await harness.DispatchAsync(session, "CMD_GET_PLAYERINFO");
        Assert.Equal(3, info[0].GetInt32("birthmonth"));
        Assert.Equal(21, info[0].GetInt32("birthday"));
        Assert.Equal(7, info[0].GetInt32("country"));
        Assert.Equal(11, info[0].GetInt32("favoriteTeam"));
        Assert.Equal(1234, info[0].GetInt32("favoritePlayer"));
        Assert.Equal("hello there", info[0].GetString("intro"));
    }

    [Fact]
    public async Task Profile_edit_without_a_selected_player_is_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Menu, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_SET_PLAYERPROFILE", withFields: r =>
            r.Set("profile", new KvMessage().Set("country", 7)));

        Assert.Equal("ERR_DATABASE", replies[0].GetString("result"));
    }
}

public class SessionStateTests
{
    [Fact]
    public async Task Commands_arriving_too_early_are_rejected_rather_than_handled()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby);   // still Connected

        var replies = await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        Assert.Equal("ERR", replies[0].GetString("result"));
        Assert.Equal("SEQUENCE", replies[0].GetString("reason"));
    }

    [Fact]
    public async Task Challenge_then_auth_advances_the_session()
    {
        await using var harness = await ServerHarness.CreateAsync();
        const string credential = "86d84f975c5afebdea53f5ec3c6abbde";

        await using (var scope = Microsoft.Extensions.DependencyInjection
                     .ServiceProviderServiceExtensions.CreateAsyncScope(harness.Services))
        {
            var accounts = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Data.Repositories.IAccountRepository>(scope.ServiceProvider);
            await accounts.RegisterAsync("tester", credential, "SERIAL-TESTER");
        }

        Assert.True((await harness.AuthenticateHttpAsync("tester", credential)).Success);

        var session = harness.NewSession(ServiceRole.Account);

        var challenge = await harness.DispatchAsync(session, "MSG_REQCCODE");
        Assert.Equal(SessionState.Challenged, session.State);
        Assert.Equal("MSG_CHALLENGE", challenge[1].MsgName);
        Assert.Equal(32, challenge[1].GetString("ccode")!.Length);

        var auth = await harness.AuthenticateSocketAsync(
            session, "tester", credential, "SERIAL-TESTER");

        Assert.Equal(SessionState.Authenticated, session.State);
        Assert.Equal("MSG_AUTHRESULT", auth[1].MsgName);
        Assert.Equal("tester", session.GameId);
    }

    [Fact]
    public async Task Selecting_a_player_binds_the_session_to_a_pid()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        Assert.Equal(SessionState.PlayerSelected, session.State);
        Assert.Equal("Local Player", session.PlayerName);
        Assert.True(session.Pid > 0);
    }
}

public class RoomTests
{
    [Fact]
    public async Task Room_member_list_is_built_from_live_sessions()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        host.PlayerName = "Host";
        host.ExternalIp = "203.0.113.7";
        host.ExternalPort = 5730;

        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");
        Assert.True(roomId > 0);
        Assert.Equal(SessionState.InRoom, host.State);

        var guest = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        guest.Pid = 2;
        guest.PlayerName = "Guest";
        await harness.DispatchAsync(guest, "CMD_JOIN_ROOM", withFields: r =>
            r.Set("room_id", roomId));

        var replies = await harness.DispatchAsync(host, "MSG_REQRMEMBERLIST");
        var members = replies.Single(r => r.MsgName == "MSG_RMEMBERLIST").GetList("list");

        Assert.Equal(2, members.Count);
        Assert.Equal("203.0.113.7", members[0].GetString("ex_ip"));
        Assert.Equal([0, 1], members.Select(m => m.GetInt32("room_entry_no")).ToArray());
        Assert.Equal([1, 2], members.Select(m => m.GetInt32("pid")).ToArray());

        // room_pid is the host, identical on every entry — pid already carries the member.
        Assert.All(members, m => Assert.Equal(1, m.GetInt32("room_pid")));
    }

    [Fact]
    public async Task Leaving_the_last_session_disposes_the_room()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;

        await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        Assert.Single(harness.Hub.Rooms);

        harness.Hub.Unregister(host);
        Assert.Empty(harness.Hub.Rooms);
    }

    [Fact]
    public async Task A_handler_can_push_to_another_session()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        var created = await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        var roomId = created[0].GetInt32("room_id");

        var guest = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        guest.Pid = 2;
        harness.Hub.JoinRoom(guest, roomId, password: "");

        var sent = harness.Hub.BroadcastToRoom(
            roomId, KvMessage.Ok("MSG_TESTNOTICE", 1), except: host.Id);

        Assert.Equal(1, sent);
        Assert.True(guest.Queue.Reader.TryRead(out var queued));
        Assert.Equal("MSG_TESTNOTICE", queued.Message!.MsgName);
    }
}

public class BlockTests
{
    [Fact]
    public async Task Joining_a_block_records_both_endpoints()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r
            .Set("ex_ip", "198.51.100.9")
            .Set("ex_port", 6000)
            .Set("in_ip", "192.168.1.44")
            .Set("in_port", 6001));

        Assert.Equal("198.51.100.9", session.ExternalIp);
        Assert.Equal(6000, session.ExternalPort);
        Assert.Equal("192.168.1.44", session.InternalIp);
        Assert.Equal(6001, session.InternalPort);
        Assert.Equal(SessionState.InBlock, session.State);
    }

    [Fact]
    public async Task Block_player_list_reports_the_players_present()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");
        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        var replies = await harness.DispatchAsync(session, "MSG_REQBLOCKPLAYERLIST");

        Assert.Equal(
            ["MSG_REQBLOCKPLAYERLIST", "MSG_BLOCKPLAYERLISTSTART", "MSG_BLOCKPLAYERLISTDATA",
             "MSG_BLOCKPLAYERLISTEND"],
            replies.Select(r => r.MsgName ?? "").ToArray());

        var data = replies[2];
        Assert.Equal(1, data.GetInt32("block_player_num"));
        Assert.Equal("Local Player", data.GetList("block_player_list")[0].GetString("name"));
    }

    [Fact]
    public async Task Block_player_list_reports_the_room_occupied_by_a_player()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");
        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");
        var room = Assert.Single(await harness.DispatchAsync(session, "CMD_CREATEJOIN_ROOM"));

        var replies = await harness.DispatchAsync(session, "MSG_REQBLOCKPLAYERLIST");
        var entry = Assert.Single(replies[2].GetList("block_player_list"));

        Assert.Equal(room.GetInt32("room_id"), entry.GetInt32("room_id"));
    }
}

public class MultiPortIdentityTests
{
    [Fact]
    public async Task A_second_connection_inherits_the_identity_of_the_first()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var account = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(account, "CMD_SET_CURRENTPLAYER");

        // The client opens a fresh socket for the lobby port and does not repeat login.
        var lobby = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.Lobby,
            Remote = account.Remote,
        };

        Assert.True(harness.Hub.TryAdoptIdentity(lobby));
        Assert.Equal(account.Pid, lobby.Pid);
        Assert.Equal("Local Player", lobby.PlayerName);
        Assert.Equal(SessionState.PlayerSelected, lobby.State);
    }

    [Fact]
    public async Task Identity_is_not_shared_across_different_addresses()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var account = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(account, "CMD_SET_CURRENTPLAYER");

        var elsewhere = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.Lobby,
            Remote = new System.Net.IPEndPoint(System.Net.IPAddress.Parse("10.0.0.5"), 50001),
        };

        Assert.False(harness.Hub.TryAdoptIdentity(elsewhere));
        Assert.Equal(0, elsewhere.Pid);
    }

    [Fact]
    public async Task Room_membership_is_never_inherited()
    {
        await using var harness = await ServerHarness.CreateAsync();

        var host = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        host.Pid = 1;
        await harness.DispatchAsync(host, "CMD_CREATEJOIN_ROOM");
        Assert.Equal(SessionState.InRoom, host.State);

        var second = new Session
        {
            Id = Guid.NewGuid(),
            Role = ServiceRole.VdpChat,
            Remote = host.Remote,
        };

        Assert.True(harness.Hub.TryAdoptIdentity(second));
        Assert.Equal(SessionState.PlayerSelected, second.State);
        Assert.Null(second.RoomId);
    }
}

public class UnknownCommandTests
{
    [Fact]
    public async Task Unknown_commands_fall_back_to_a_bare_ack()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession();

        var replies = await harness.DispatchAsync(session, "CMD_TOTALLY_UNKNOWN");

        Assert.Equal("result=\"NOERR\",msg=\"CMD_TOTALLY_UNKNOWN\",rqid=4\0",
            harness.Render(replies[0]));
    }
}

public class SessionCommandTests
{
    [Fact]
    public async Task Heartbeat_is_acknowledged_and_stamps_activity()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);

        session.LastActivity = DateTimeOffset.UtcNow.AddMinutes(-10);
        var replies = await harness.DispatchAsync(session, "CMD_SEND_HEARTBEAT");

        Assert.Equal("result=\"NOERR\",msg=\"CMD_SEND_HEARTBEAT\",rqid=4\0",
            harness.Render(replies[0]));
        Assert.True(DateTimeOffset.UtcNow - session.LastActivity < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Language_is_stored_on_the_session_and_the_player()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);
        await harness.DispatchAsync(session, "CMD_SET_CURRENTPLAYER");

        await harness.DispatchAsync(session, "CMD_SET_LANGUAGE", withFields: r => r.Set("lang", "FR"));

        Assert.Equal("FR", session.Language);

        var info = await harness.DispatchAsync(session, "CMD_GET_PLAYERINFO");
        Assert.Equal("FR", info[0].GetString("lang"));
    }
}

public class GameEnvironmentTests
{
    [Fact]
    public async Task Room_settings_are_kept_verbatim()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InBlock);
        session.Pid = 1;

        var created = await harness.DispatchAsync(session, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Test Room")
            .Set("match_type", "OC_FREE")
            .Set("team_category", "ALL")
            .Set("max_players", 4)
            .Set("enable_guest", "YES"));

        var room = harness.Hub.RequireRoom(created[0].GetInt32("room_id"));
        Assert.Equal("OC_FREE", room.MatchType);
        Assert.Equal("ALL", room.TeamCategory);
        Assert.True(room.AllowGuest);

        await harness.DispatchAsync(session, "CMD_SET_GAMEENV", withFields: r =>
            r.Set("game_env", new KvMessage()
                .Set("cpuLevel", "HARD")
                .Set("gametime", "5MINUTES")
                .Set("ball_type", 7)
                .Set("substitution", 6)));

        Assert.NotNull(room.GameEnvironment);
        Assert.Equal("HARD", room.GameEnvironment!.GetString("cpuLevel"));
        Assert.Equal(7, room.GameEnvironment.GetInt32("ball_type"));
        Assert.Equal(6, room.GameEnvironment.GetInt32("substitution"));
    }

    [Fact]
    public async Task Game_settings_outside_a_room_are_refused()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.InRoom);

        var replies = await harness.DispatchAsync(session, "CMD_SET_GAMEENV", withFields: r =>
            r.Set("game_env", new KvMessage().Set("cpuLevel", "HARD")));

        Assert.Equal("NOROOM", replies[0].GetString("reason"));
    }
}

public class LobbyConfigurationTests
{
    private static void ThreeLobbies(Dictionary<string, string?> settings)
    {
        settings["Server:Lobbies:0:Name"] = "Beginner";
        settings["Server:Lobbies:0:MaxPlayers"] = "100";
        settings["Server:Lobbies:1:Name"] = "Standard";
        settings["Server:Lobbies:1:MaxPlayers"] = "64";
        settings["Server:Lobbies:2:Name"] = "Expert";
        settings["Server:Lobbies:2:MaxPlayers"] = "32";
    }

    [Fact]
    public async Task Block_list_comes_from_configuration()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_GET_BLOCKLIST");
        var blocks = replies[0].GetList("bklist");

        Assert.Equal(3, replies[0].GetInt32("count"));
        Assert.Equal(["Beginner", "Standard", "Expert"],
            blocks.Select(b => b.GetString("name") ?? "").ToArray());
        Assert.Equal(32, blocks[2].GetInt32("max_player_num"));
    }

    [Fact]
    public async Task A_disabled_lobby_is_not_advertised()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            ThreeLobbies(s);
            s["Server:Lobbies:1:Enabled"] = "false";
        });
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_GET_BLOCKLIST");

        Assert.Equal(["Beginner", "Expert"],
            replies[0].GetList("bklist").Select(b => b.GetString("name") ?? "").ToArray());
    }

    [Fact]
    public async Task Join_selects_the_block_by_its_index_in_the_advertised_list()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 2));

        Assert.Equal(SessionState.InBlock, session.State);
        Assert.Equal(3, session.BlockId);       // third entry, auto-assigned id
    }

    [Fact]
    public async Task Occupancy_is_reported_per_block()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);

        var first = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        first.Pid = 1;
        await harness.DispatchAsync(first, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 0));

        var second = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);
        second.Pid = 2;
        await harness.DispatchAsync(second, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 2));

        var replies = await harness.DispatchAsync(first, "CMD_GET_BLOCKLIST");
        var blocks = replies[0].GetList("bklist");

        Assert.Equal(1, blocks[0].GetInt32("player_num"));
        Assert.Equal(0, blocks[1].GetInt32("player_num"));
        Assert.Equal(1, blocks[2].GetInt32("player_num"));
    }

    [Fact]
    public async Task Explicit_ids_survive_a_reorder()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
        {
            s["Server:Lobbies:0:Name"] = "Expert";
            s["Server:Lobbies:0:Id"] = "77";
            s["Server:Lobbies:1:Name"] = "Beginner";
            s["Server:Lobbies:1:Id"] = "42";
        });
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r => r.Set("index", 1));

        Assert.Equal(42, session.BlockId);
    }

    [Fact]
    public async Task Join_falls_back_to_the_name_when_no_index_is_given()
    {
        await using var harness = await ServerHarness.CreateAsync(ThreeLobbies);
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        await harness.DispatchAsync(session, "CMD_JOIN_BLOCK", withFields: r =>
            r.Set("name", "Standard"));

        Assert.Equal(2, session.BlockId);
    }

    [Fact]
    public async Task Join_is_refused_when_no_lobbies_are_configured()
    {
        await using var harness = await ServerHarness.CreateAsync(s =>
            s["Server:Lobbies:0:Enabled"] = "false");
        var session = harness.NewSession(ServiceRole.Lobby, SessionState.Authenticated);

        var replies = await harness.DispatchAsync(session, "CMD_JOIN_BLOCK");

        Assert.Equal("NOBLOCK", replies[0].GetString("reason"));
        Assert.Equal(SessionState.Authenticated, session.State);
    }
}

public class RegistryTests
{
    [Fact]
    public async Task Every_registered_command_is_reachable_on_at_least_one_service()
    {
        await using var harness = await ServerHarness.CreateAsync();
        var registry = (Dispatch.CommandRegistry)harness.Services.GetService(
            typeof(Dispatch.CommandRegistry))!;

        Assert.NotEmpty(registry.Entries);
        Assert.All(registry.Entries, entry => Assert.NotEqual(ServiceRole.None, entry.Roles));
    }
}

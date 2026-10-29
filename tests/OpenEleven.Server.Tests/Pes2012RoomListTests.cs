// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 PES5 Indie Team <pes5indie@gmail.com>
//
// This file is part of OpenEleven. See the LICENSE file at the repository root.

using Microsoft.Extensions.DependencyInjection;
using OpenEleven.Protocol.Kv;
using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Tests;

public class Pes2012RoomListTests
{
    [Fact]
    public async Task A_new_room_is_published_as_ADD_to_the_other_block_watcher()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var owner = Player(harness, 1);
        var watcher = Player(harness, 2);

        var initial = await harness.DispatchAsync(watcher, "CMD_REQWATCHL_ROOMLIST", rqid: 20, withFields: r => r
            .Set("listnum_name", "room_list_num").Set("list_name", "room_list"));
        Assert.Empty(Assert.Single(initial).GetList("room_list"));
        // The creator also keeps its room-list subscription after entering the room.
        await harness.DispatchAsync(owner, "CMD_REQWATCHL_ROOMLIST", rqid: 21, withFields: r => r
            .Set("listnum_name", "room_list_num").Set("list_name", "room_list"));

        var createReplies = await harness.DispatchAsync(owner, "CMD_CREATEJOIN_ROOM", withFields: r => r
            .Set("name", "Room marcos1").Set("max_players", 2).Set("match_type", "OC_RANKING"));
        var roomId = Assert.Single(createReplies, m => m.MsgName == "CMD_CREATEJOIN_ROOM").GetInt32("room_id");
        Assert.True(roomId > 0);

        // The watcher's own queue gets the ADD.
        var added = Assert.Single(Drain(watcher), m => m.MsgName == "CMD_REQWATCHL_ROOMLIST");
        Assert.Equal(20, added.Rqid);
        Assert.Equal("ADD", added.GetString("list_type"));
        var entry = Assert.Single(added.GetList("room_list"));
        Assert.Equal(roomId, entry.GetInt32("room_id"));
        Assert.Equal("Room marcos1", entry.GetString("name"));
        Assert.Equal(1, entry.GetInt32("gamer_num"));
        Assert.Equal(1, Assert.Single(entry.GetList("gamer")).GetInt32("pid"));
        // PES2012 reads team[0..1], team_flag[0..2] and score[0..1] as paths into arrays. Its
        // tokenizer splits on '[', so one literal key[i]= fails the parse of the whole message.
        var wire = harness.Render(added);
        Assert.Contains("team=[0,0]", wire);
        Assert.Contains("team_flag=[", wire);
        Assert.Contains("score=[{", wire);
        Assert.Contains("score_1st=0", wire);
        Assert.DoesNotMatch(@"\w\[\d+\]=", wire);

        // The client's frame reader only takes ~1015 payload bytes per frame; above that the
        // server switches to the 0x0070/0x0071/0x0072 chunk sequence. That path is only
        // verified against pes2011, so a room row must stay inside one frame.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(wire);
        Assert.True(bytes <= 1015,
            $"room-list reply is {bytes} bytes; it no longer fits a single frame (<=1015) and gets chunked");
        Assert.Single(OpenEleven.Protocol.Framing.InnerBody.WrapChunked(wire));

        // The creator's room-list cache also learns about its own room.
        Assert.Contains(createReplies, m => m.MsgName == "CMD_REQWATCHL_ROOMLIST"
                                           && m.GetString("list_type") == "ADD"
                                           && m.GetList("room_list")[0].GetInt32("room_id") == roomId);

        // In-room user list: PES2012 reads desiredPosition[0..13] (an array of 14) and gameplayer_id.
        var room = harness.Hub.RequireRoom(roomId);
        var member = Assert.Single(harness.Hub.RoomPlayerEntries(room, owner));
        Assert.Equal(14, Assert.IsType<KvArray>(member.GetValue("desiredPosition")).Values.Count);
        Assert.True(member.Has("gameplayer_id"));
        Assert.True(member.Has("division"));
        Assert.DoesNotMatch(@"\w\[\d+\]=", harness.Render(member));
    }

    // FUN_0053ed70 reads the reply all or nothing; a missing key also drops useable_team_id.
    [Fact]
    public async Task Current_player_reply_carries_every_key_pes2012_requires()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var session = harness.NewSession(ServiceRole.Account, SessionState.Authenticated);

        var wire = await harness.RenderFirstAsync(session, "CMD_SET_CURRENTPLAYER");

        foreach (var key in new[]
                 {
                     "useable_team_size", "mailcount", "newmail", "chat_probi", "camera_probi", "voice_probi",
                     "band_probi", "green_ping", "green_band", "green_loss", "yellow_ping", "yellow_band",
                     "yellow_loss", "red_ping", "red_band", "red_loss", "green_delay", "yellow_delay", "red_delay",
                     "p2ptimeout", "first_login", "lossframe_match", "avg_max_combination", "avg_total_combination",
                     "invite_num", "check_lobbygame_ping_diff", "check_game_ping_max", "mcount10flg",
                     "delay_frame_2players", "delay_frame_4players", "max_input_delay_frames", "proto_opt",
                 })
            Assert.Contains($",{key}=", wire);

        Assert.Contains("useable_team_size=335,useable_team_id=[21,", wire);
        Assert.Contains("proto_opt=\"0\"", wire);
    }

    [Fact]
    public async Task The_owner_takes_game_slot_0_even_when_the_guest_enters_first()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var owner = Player(harness, 1);
        var guest = Player(harness, 2);
        var room = harness.Hub.CreateRoom(owner, "Room marcos1", 2);
        harness.Hub.JoinRoom(guest, room.Id, "");

        room.SetGameEntry(guest, entered: true, side: 1);
        Assert.Equal(0, guest.GameEntryNo);

        room.SetGameEntry(owner, entered: true, side: 0);
        Assert.Equal(0, owner.GameEntryNo);
        Assert.Equal(1, guest.GameEntryNo);
    }

    [Fact]
    public async Task An_uploaded_avatar_is_served_to_other_players_and_a_missing_one_is_size_0()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var owner = Player(harness, 1);
        var viewer = Player(harness, 2);

        var none = Assert.Single(await harness.DispatchAsync(viewer, "CMD_GET_AVATAR", withFields: r => r.Set("pid", 1)));
        Assert.Equal(0, none.GetInt32("avatar_data_size"));
        Assert.False(none.Has("avatar_data"));

        var hex = Convert.ToHexString(new byte[] { 1, 2, 3, 4 });
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant();
        await harness.DispatchAsync(owner, "CMD_SET_AVATAR", withFields: r => r
            .Set("avatar_data_size", 4).Set("avatar_data", hex).Set("avatar_md5", md5));

        var got = Assert.Single(await harness.DispatchAsync(viewer, "CMD_GET_AVATAR", withFields: r => r.Set("pid", 1)));
        Assert.Equal(4, got.GetInt32("avatar_data_size"));
        Assert.Equal(hex, got.GetString("avatar_data"));
        Assert.Equal(md5, got.GetString("avatar_md5"));
    }

    [Fact]
    public async Task Community_lists_answer_with_an_explicit_zero_count()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var player = Player(harness, 1);

        var joined = Assert.Single(await harness.DispatchAsync(player, "CMD_GET_JOINEDCOMMULIST"));
        var cards = Assert.Single(await harness.DispatchAsync(player, "CMD_GET_COMMU_CARDLIST", withFields: r => r.Set("target_pid", 1)));

        Assert.Equal((true, 0, true, 0), (joined.Has("commu_num"), joined.GetInt32("commu_num", -1),
            cards.Has("commu_intro_list_num"), cards.GetInt32("commu_intro_list_num", -1)));
    }

    [Fact]
    public async Task Pes2011_joined_community_list_answers_with_an_explicit_zero_count()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2011Pc");
        var joined = Assert.Single(await harness.DispatchAsync(Player(harness, 1), "CMD_GET_JOINEDCOMMULIST"));

        Assert.Equal((true, 0), (joined.Has("commu_num"), joined.GetInt32("commu_num", -1)));
    }

    [Fact]
    public async Task Each_players_playtime_report_is_kept_on_the_match()
    {
        await using var harness = await ServerHarness.CreateAsync(s => s["Server:GameProfile"] = "Pes2012Pc");
        var owner = Player(harness, 1);
        var guest = Player(harness, 2);
        var room = harness.Hub.CreateRoom(owner, "Room marcos1", 2);
        harness.Hub.JoinRoom(guest, room.Id, "");
        var matchId = await harness.Services.GetRequiredService<MatchResults>().StartAsync(room);

        KvMessage Footballer(int id, int minutes, string position)
            => new KvMessage().Set("gameplayer_id", id).Set("play_time", minutes).Set("position", position);
        var list = new[] { Footballer(34437, 90, "GK"), Footballer(7359, 0, "CF"), Footballer(3582, 75, "CF") };
        await harness.DispatchAsync(owner, "CMD_SET_GAMEPLAYER_PLAYTIME", withFields: r => r.Set("player_list_num", 3).Set("player_list", list));
        await harness.DispatchAsync(guest, "CMD_SET_GAMEPLAYER_PLAYTIME", withFields: r => r.Set("player_list_num", 3).Set("player_list", list));
        await harness.DispatchAsync(owner, "CMD_SET_GAMEPLAYER_PLAYTIME", withFields: r => r.Set("player_list_num", 3).Set("player_list", list));

        await using var scope = harness.Services.CreateAsyncScope();
        var record = await scope.ServiceProvider.GetRequiredService<OpenEleven.Data.GameDbContext>().Matches.FindAsync((long)matchId);
        Assert.Equal("2|34437|GK|90\n2|3582|CF|75\n1|34437|GK|90\n1|3582|CF|75", record!.PlayTimes);
    }

    private static Session Player(ServerHarness harness, int pid)
    {
        var s = harness.NewSession(ServiceRole.Menu, SessionState.InBlock);
        s.Pid = pid;
        s.PlayerName = $"marcos{pid}";
        return s;
    }

    private static List<KvMessage> Drain(Session s)
    {
        var pushed = new List<KvMessage>();
        while (s.Queue.Reader.TryRead(out var item))
            if (item.Message is { } m)
                pushed.Add(m);
        return pushed;
    }
}

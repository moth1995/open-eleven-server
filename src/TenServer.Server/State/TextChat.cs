namespace TenServer.Server.State;

public enum TextChatScene
{
    Lobby,
    Room,
}

public enum TextChatChannel
{
    Block,
    Room,
    Team,
    GameQuick,
    TeamQuick,
    Competition,
    Community,
}

public enum TextChatListMode
{
    ChatBan,
    ChatSend,
}

public sealed record TextChatSubscription(int Rqid, TextChatScene Scene);

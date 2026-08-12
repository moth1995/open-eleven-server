namespace TenServer.Server.State;

/// <summary>
/// Ordered login progression. Dispatch compares the session's current value against the
/// minimum declared on a command, so a packet arriving out of sequence is rejected with
/// a logged reason instead of being handled against half-initialised state.
/// </summary>
public enum SessionState
{
    Connected = 0,
    EulaAccepted = 10,
    Challenged = 20,
    Authenticated = 30,
    PlayerSelected = 40,
    InBlock = 50,
    InRoom = 60,
    Matching = 70,
    InMatch = 80,
}

using OpenEleven.Server.Configuration;
using OpenEleven.Server.State;

namespace OpenEleven.Server.Dispatch;

/// <summary>
/// Marks a handler method for one protocol command. Adding a command means adding a
/// method; nothing in the dispatch core changes.
/// Methods must have the signature
/// <c>ValueTask&lt;KvMessage[]&gt; Name(CommandContext ctx)</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CommandAttribute(string msg) : Attribute
{
    public string Msg { get; } = msg;

    /// <summary>Service ports this command is accepted on.</summary>
    public ServiceRole Roles { get; init; } = ServiceRole.All;

    /// <summary>Minimum session state; anything earlier is rejected before the handler runs.</summary>
    public SessionState RequiredState { get; init; } = SessionState.Connected;
}

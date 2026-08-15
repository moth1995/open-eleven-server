using OpenEleven.Server.Configuration;

namespace OpenEleven.Profiles.Pes2011Pc;

/// <summary>Marks the Pes2011Pc profile assembly so the host can discover it.</summary>
public sealed class Pes2011PcProfile : IProfileMarker
{
    public GameProfile Profile => GameProfile.Pes2011Pc;
}
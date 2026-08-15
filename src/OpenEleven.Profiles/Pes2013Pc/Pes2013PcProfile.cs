using OpenEleven.Server.Configuration;

namespace OpenEleven.Profiles.Pes2013Pc;

/// <summary>Marks the Pes2013Pc profile assembly so the host can discover it.</summary>
public sealed class Pes2013PcProfile : IProfileMarker
{
    public GameProfile Profile => GameProfile.Pes2013Pc;
}
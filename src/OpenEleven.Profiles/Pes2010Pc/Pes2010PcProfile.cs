using OpenEleven.Server.Configuration;

namespace OpenEleven.Profiles.Pes2010Pc;

/// <summary>Marks the Pes2010Pc profile assembly so the host can discover it.</summary>
public sealed class Pes2010PcProfile : IProfileMarker
{
    public GameProfile Profile => GameProfile.Pes2010Pc;
}
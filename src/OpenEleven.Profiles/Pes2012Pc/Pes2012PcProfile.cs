using OpenEleven.Server.Configuration;

namespace OpenEleven.Profiles.Pes2012Pc;

/// <summary>Marks the Pes2012Pc profile assembly so the host can discover it.</summary>
public sealed class Pes2012PcProfile : IProfileMarker
{
    public GameProfile Profile => GameProfile.Pes2012Pc;
}
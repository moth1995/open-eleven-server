namespace OpenEleven.Server.Configuration;

/// <summary>
/// The game title a server process serves. One process serves exactly one title, so a
/// configured value must always be a single flag; <see cref="All"/> exists only as the
/// default filter on <c>CommandAttribute.Profiles</c> for commands shared by every title.
/// </summary>
[Flags]
public enum GameProfile
{
    Pes2010Pc = 1 << 0,
    Pes2011Pc = 1 << 1,
    Pes2012Pc = 1 << 2,
    Pes2013Pc = 1 << 3,

    All = Pes2010Pc | Pes2011Pc | Pes2012Pc | Pes2013Pc,
}

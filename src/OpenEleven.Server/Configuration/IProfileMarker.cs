namespace OpenEleven.Server.Configuration;

/// <summary>
/// Implemented once per per-title profile assembly. The host locates the referenced
/// profile assembly by finding its <see cref="IProfileMarker"/> and uses the assembly it
/// lives in as the extra command-registry scan source.
/// </summary>
public interface IProfileMarker
{
    GameProfile Profile { get; }
}
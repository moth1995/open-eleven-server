using OpenEleven.Protocol.Crypto;

namespace OpenEleven.Server.Web;

/// <summary>
/// The HTTP half of login. The client posts a form and parses three LF-separated lines
/// back; a leading "1" is what its transport turns into an internal NOERR.
/// </summary>
public static class GameIdAuthEndpoint
{
    public static bool LooksLikeMd5(string value)
        => AuthProof.TryNormalizeMd5(value, out _);

    public static string BuildSuccessResponse(string gameId, string credential)
        => $"1\n{gameId.Replace("\r", "").Replace("\n", "")}\n{credential}\n";

    public static string BuildFailureResponse() => "0\n\n\n";
}

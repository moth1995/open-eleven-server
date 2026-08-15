namespace OpenEleven.Protocol.Crypto;

/// <summary>
/// The fixed cipher keys shared by every supported title. These are not secrets: they
/// ship inside every copy of the game client. All four supported titles (PES 2010-2013,
/// PC) use the same XOR and Blowfish keys, so they are compiled in rather than carried
/// in per-title configuration.
/// </summary>
public static class GameCrypto
{
    /// <summary>4-byte rolling XOR key, hex.</summary>
    public const string XorKey = "5B9F2E64";

    /// <summary>56-byte Blowfish key, hex.</summary>
    public const string BlowfishKey =
        "D8890AF066C96B40D701AEFC436FF9FEC98998167A74483D" +
        "3914730C5C01C03CE28E86E589C4A185F8540651D2ECA36B" +
        "5C1A40EEC5E9DAAE";
}

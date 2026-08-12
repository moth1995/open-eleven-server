using TenServer.Protocol.Crypto;
using Xunit;

namespace TenServer.Protocol.Tests;

public class AuthProofTests
{
    [Fact]
    public void Matches_the_observed_client_vector()
    {
        var hash = AuthProof.Compute(
            "86d84f975c5afebdea53f5ec3c6abbde",
            "00112233445566778899aabbccddeeff",
            "local-player");

        Assert.Equal("ed9191ecc473909797f1f24d627cc6e3", hash);
    }

    [Theory]
    [InlineData("00112233445566778899aabbccddeeff", true)]
    [InlineData("00112233445566778899AABBCCDDEEFF", true)]
    [InlineData("001122", false)]
    [InlineData("00112233445566778899aabbccddeefg", false)]
    public void Challenge_requires_exactly_sixteen_hex_bytes(string challenge, bool valid)
        => Assert.Equal(valid, AuthProof.TryDecodeChallenge(challenge, out _));
}

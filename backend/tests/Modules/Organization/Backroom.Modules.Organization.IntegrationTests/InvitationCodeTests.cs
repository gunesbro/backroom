using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Backroom.Modules.Organization.Infrastructure.Invitations;

namespace Backroom.Modules.Organization.IntegrationTests;

/// <summary>The invitation code and its keys (docs/contracts/phase-1.md, "Invitation security").</summary>
public sealed class InvitationCodeTests
{
    [Fact]
    public void Derive_IsTheUrlSafeMacOfTheIdAndTheSalt()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var salt = RandomNumberGenerator.GetBytes(InvitationCode.SaltLength);
        var id = Guid.Parse("01938a6e-7d2b-7c44-9d0a-0123456789ab");
        var code = new byte[InvitationCode.Length];

        InvitationCode.Derive(key, id, salt, code);

        // The id as the UUID is written, then the salt.
        byte[] message = [.. Convert.FromHexString("01938a6e7d2b7c449d0a0123456789ab"), .. salt];
        Encoding.ASCII.GetString(code).Should().Be(Base64Url.EncodeToString(HMACSHA256.HashData(key, message)));
        Encoding.ASCII.GetString(code).Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }

    /// <summary>
    /// A fixed vector computed outside .NET (PowerShell's HMACSHA256 over the id's hex bytes and the salt), for the
    /// control plane and the Email module to check their side against: key 00..1f, salt 64..73.
    /// </summary>
    [Fact]
    public void Derive_MatchesTheKnownAnswer()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var salt = Enumerable.Range(100, 16).Select(value => (byte)value).ToArray();
        var code = new byte[InvitationCode.Length];

        InvitationCode.Derive(key, Guid.Parse("01938a6e-7d2b-7c44-9d0a-0123456789ab"), salt, code);

        Encoding.ASCII.GetString(code).Should().Be("ZvGRZty2ZzxBt7v_42gI2GABt096p_MUtGROgpUWRBg");
        Convert.ToHexStringLower(InvitationCode.Hash(code)).Should().Be("2755cd569d94308b849d7b1c0d167bb90b0d4d97956e00b24e82853075753cd5");
    }

    [Fact]
    public void Issue_KeepsOnlyTheHashOfACodeThatCanBeDerivedAgain()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var issuer = new InvitationCodeIssuer(new InvitationKeyRing(Options.Create(OptionsWith(3, (3, key), (2, RandomNumberGenerator.GetBytes(32))))));
        var id = Guid.CreateVersion7();

        var first = issuer.Issue(id);
        var second = issuer.Issue(id);

        first.KeyVersion.Should().Be(3);
        first.Salt.Should().HaveCount(16);
        second.Salt.Should().NotEqual(first.Salt, "every code has a new random salt");
        second.CodeHash.Should().NotEqual(first.CodeHash);
        var code = new byte[InvitationCode.Length];
        InvitationCode.Derive(key, id, first.Salt, code);
        first.CodeHash.Should().Equal(SHA256.HashData(code), "the link is built later from the id, the salt and the key");
    }

    [Fact]
    public void Validate_WithGoodKeys_Passes()
    {
        var result = new InvitationKeyOptionsValidator().Validate(null, OptionsWith(2, (1, RandomNumberGenerator.GetBytes(32)), (2, RandomNumberGenerator.GetBytes(48))));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithoutKeys_Fails()
    {
        var result = new InvitationKeyOptionsValidator().Validate(null, new InvitationKeyOptions());

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenTheCurrentVersionHasNoKey_Fails()
    {
        var result = new InvitationKeyOptionsValidator().Validate(null, OptionsWith(2, (1, RandomNumberGenerator.GetBytes(32))));

        result.Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("one")]
    [InlineData(" 1")]
    public void Validate_WithAVersionThatIsNotAPositiveNumber_Fails(string version)
    {
        var options = new InvitationKeyOptions { CurrentVersion = 1 };
        options.Keys[version] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        new InvitationKeyOptionsValidator().Validate(null, options).Failed.Should().BeTrue();
    }

    [Fact]
    public void Validate_WithAShortOrMalformedKey_FailsWithoutQuotingIt()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(31));
        var tooShort = new InvitationKeyOptions { CurrentVersion = 1 };
        tooShort.Keys["1"] = shortKey;
        var malformed = new InvitationKeyOptions { CurrentVersion = 1 };
        malformed.Keys["1"] = "not base64, but long enough to look like a key!!";

        var shortResult = new InvitationKeyOptionsValidator().Validate(null, tooShort);
        var malformedResult = new InvitationKeyOptionsValidator().Validate(null, malformed);

        shortResult.Failed.Should().BeTrue();
        shortResult.FailureMessage.Should().NotContain(shortKey);
        malformedResult.Failed.Should().BeTrue();
        malformedResult.FailureMessage.Should().NotContain("not base64");
    }

    [Fact]
    public void KeyRing_WithBadKeys_Throws()
    {
        var create = () => new InvitationKeyRing(Options.Create(new InvitationKeyOptions()));

        create.Should().Throw<OptionsValidationException>();
    }

    private static InvitationKeyOptions OptionsWith(int currentVersion, params (int Version, byte[] Key)[] keys)
    {
        var options = new InvitationKeyOptions { CurrentVersion = currentVersion };
        foreach (var (version, key) in keys)
        {
            options.Keys[version.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Convert.ToBase64String(key);
        }

        return options;
    }
}

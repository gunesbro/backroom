using AwesomeAssertions;
using Backroom.BuildingBlocks.Domain.Email;
using System.Globalization;

namespace Backroom.BuildingBlocks.UnitTests.Email;

/// <summary>Email addresses: exactly one, normalized the same way under every culture (docs/contracts/phase-1.md, "Cross-cutting rules").</summary>
public sealed class EmailAddressTests
{
    [Theory]
    [InlineData("new.person@example.test", "new.person@example.test")]
    [InlineData("  New.Person@Example.TEST  ", "new.person@example.test")]
    [InlineData("o'neil+tag@example.test", "o'neil+tag@example.test")]
    [InlineData("kim@BÜCHER.example", "kim@xn--bcher-kva.example")]
    [InlineData("kim@xn--bcher-kva.example", "kim@xn--bcher-kva.example")]
    // IDNA 2008 as ICU implements it keeps ß; NLS (IDNA 2003) would map it to "ss", another domain. A host that
    // does not use ICU fails here.
    [InlineData("alice@straße.de", "alice@xn--strae-oqa.de")]
    public void TryNormalize_WithOneAddress_NormalizesIt(string input, string expected)
    {
        EmailAddress.TryNormalize(input, out var email).Should().BeTrue();

        email.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign.example.test")]
    [InlineData("@example.test")]
    [InlineData("kim@")]
    [InlineData("kim@localhost")]
    [InlineData("kim@192.168.1.10")]
    [InlineData("kim@[192.168.1.10]")]
    [InlineData("kim@-bad.example.test")]
    [InlineData("kim@example..test")]
    [InlineData("kim@example.test.")]
    [InlineData("kim@@example.test")]
    [InlineData(".kim@example.test")]
    [InlineData("kim.@example.test")]
    [InlineData("ki..m@example.test")]
    [InlineData("ki m@example.test")]
    [InlineData("kim@example.test, lee@example.test")]
    [InlineData("kim@example.test;lee@example.test")]
    [InlineData("Kim <kim@example.test>")]
    [InlineData("\"kim\"@example.test")]
    [InlineData("kim(comment)@example.test")]
    [InlineData("kim@example.test\r\nBcc: lee@example.test")]
    [InlineData("kim\n@example.test")]
    [InlineData("kim\0@example.test")]
    [InlineData("ｋｉｍ@example.test")]
    [InlineData("İpek@example.test")]
    [InlineData("ıpek@example.test")]
    [InlineData("çelik@example.test")]
    [InlineData("ki​m@example.test")]
    [InlineData("a@xn--abc.com")]
    public void TryNormalize_WithAnythingElse_RefusesIt(string? input)
    {
        EmailAddress.TryNormalize(input, out var email).Should().BeFalse();

        email.Should().BeEmpty();
    }

    [Fact]
    public void TryNormalize_WithTooLongParts_RefusesThem()
    {
        var longLocal = new string('a', 65) + "@example.test";
        var longLabel = "kim@" + new string('a', 64) + ".test";
        var longAddress = "kim@" + string.Join('.', Enumerable.Repeat(new string('a', 60), 5)) + ".test";

        EmailAddress.TryNormalize(longLocal, out _).Should().BeFalse();
        EmailAddress.TryNormalize(longLabel, out _).Should().BeFalse();
        EmailAddress.TryNormalize(longAddress, out _).Should().BeFalse("it is longer than 254 characters");
    }

    [Fact]
    public void TryNormalize_UnderTurkishCulture_LowersLikeEveryOtherCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        string email;
        try
        {
            EmailAddress.TryNormalize("ILKIN@EXAMPLE.TEST", out email).Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        email.Should().Be("ilkin@example.test", "a culture-sensitive lower case would give a dotless ı");
    }
}

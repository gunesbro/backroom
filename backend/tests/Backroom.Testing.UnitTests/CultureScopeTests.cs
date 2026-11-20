using System.Globalization;
using AwesomeAssertions;

namespace Backroom.Testing.UnitTests;

public sealed class CultureScopeTests
{
    [Fact]
    public void Turkish_WhenActive_AppliesTurkishCasingRules()
    {
        using var _ = CultureScope.Turkish();

        CultureInfo.CurrentCulture.Name.Should().Be("tr-TR");
        "I".ToLower(CultureInfo.CurrentCulture).Should().Be("ı");
        "i".ToUpper(CultureInfo.CurrentCulture).Should().Be("İ");
    }

    [Fact]
    public void Dispose_WhenCalled_RestoresPreviousCultures()
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        using (new CultureScope("de-DE"))
        {
            CultureInfo.CurrentUICulture.Name.Should().Be("de-DE");
        }

        CultureInfo.CurrentCulture.Should().Be(culture);
        CultureInfo.CurrentUICulture.Should().Be(uiCulture);
    }
}

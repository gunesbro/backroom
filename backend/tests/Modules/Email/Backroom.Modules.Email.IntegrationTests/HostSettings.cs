using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Backroom.Modules.Email.IntegrationTests;

/// <summary>Flat host settings (such as <c>environment</c>) for the services under test; sections are not needed.</summary>
internal sealed class HostSettings(IDictionary<string, string?> values) : IConfiguration
{
    public string? this[string key]
    {
        get => values.TryGetValue(key, out var value) ? value : null;
        set => values[key] = value;
    }

    public IEnumerable<IConfigurationSection> GetChildren() => [];

    public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);

    public IConfigurationSection GetSection(string key) => throw new NotSupportedException("The test host settings have no sections.");
}

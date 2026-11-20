namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>A fixed template and its version, as recorded in the delivery log (the message itself is not).</summary>
internal sealed record EmailTemplateId
{
    public const int MaxNameLength = 64;

    public EmailTemplateId(string name, int version)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxNameLength || !name.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            throw new ArgumentException($"A template name has 1 to {MaxNameLength} lower-case letters, digits and hyphens.", nameof(name));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        Name = name;
        Version = version;
    }

    public string Name { get; }

    public int Version { get; }
}

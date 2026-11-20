namespace Backroom.Modules.Email.Infrastructure.Sending;

internal enum EmailTransportMode
{
    /// <summary>Every message goes to Mailpit; the server must identify itself as Mailpit on every connection.</summary>
    Mailpit,

    /// <summary>The real server; Production with real sending allowed only.</summary>
    Real,
}

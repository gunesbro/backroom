namespace Backroom.Modules.Identity.Presentation;

/// <summary>The outcome of checking a logout token: the account and session it ends, or why it is refused (for logs; never sent to the caller).</summary>
internal sealed class LogoutTokenResult
{
    private LogoutTokenResult()
    {
    }

    public bool IsValid => Reason is null;

    public Guid AccountId { get; private init; }

    /// <summary>The Keycloak <c>sid</c>, or <see langword="null"/> when the token ends every session of the account.</summary>
    public string? SessionId { get; private init; }

    public string? Reason { get; private init; }

    public static LogoutTokenResult Valid(Guid accountId, string? sessionId) => new() { AccountId = accountId, SessionId = sessionId };

    public static LogoutTokenResult Invalid(string reason) => new() { Reason = reason };
}

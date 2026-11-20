namespace Backroom.BuildingBlocks.Presentation.Authentication;

public static class AuthorizationPolicies
{
    /// <summary>
    /// The operations panel: a token of the <c>ops</c> client, realm role <c>ops</c>, and a session
    /// authenticated with a one-time password (<c>amr</c> contains <c>otp</c>).
    /// </summary>
    public const string Operations = "operations";

    /// <summary>
    /// A cell: an access token of a cell's own client (<c>cell-{id}</c>, client credentials), validated
    /// by <see cref="AccessTokenAuthenticationExtensions.CellScheme"/>. Person tokens never pass it.
    /// </summary>
    public const string Cell = "cell";
}

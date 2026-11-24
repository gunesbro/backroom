namespace Backroom.EndToEndTests;

internal static class Gate
{
    /// <summary>Skips the test unless the end-to-end environment runs (<c>BACKROOM_E2E=true</c>).</summary>
    public static void RequireEnvironment() =>
        Assert.SkipUnless(
            EndToEndEnvironment.Enabled,
            $"End-to-end tests run only with {EndToEndEnvironment.EnabledVariable}=true (they start the AppHost and need Docker).");
}

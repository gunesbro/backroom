namespace Backroom.EndToEndTests;

/// <summary>
/// The <c>Category</c> trait: <see cref="Short"/> runs on every pull request, <see cref="Nightly"/> only in the nightly run,
/// which runs every test (docs/requirements.md, "Test stratejisi": "Her PR'da kısa set, gece tamamı").
/// </summary>
internal static class TestCategory
{
    public const string Name = "Category";

    public const string Short = "Short";

    public const string Nightly = "Nightly";
}

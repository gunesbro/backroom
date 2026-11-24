namespace Backroom.BuildingBlocks.Presentation.Cors;

/// <summary>
/// The browser origins allowed to call this host. Exact origins only (scheme, host, port); an empty list
/// means no cross-origin access. Each host (the control plane, every cell) has its own list.
/// </summary>
public sealed class WebOriginsOptions
{
    public const string SectionName = "Cors";

    /// <summary>Origins such as <c>https://app.example.test</c> or <c>http://localhost:5173</c>; no wildcards and no paths.</summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

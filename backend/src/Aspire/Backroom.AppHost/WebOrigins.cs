
namespace Backroom.AppHost;

/// <summary>The browser origins that call the control plane and the cells in development (<c>Cors:AllowedOrigins</c>).</summary>
internal static class WebOrigins
{
    /// <summary>
    /// The web dev server (it is also what the desktop app loads in development), the ops dev server (fixed ports in
    /// frontend/apps/*/vite.config.ts) and the desktop shell's release origins (Linux/macOS and Windows).
    /// </summary>
    private static readonly string[] s_origins =
    [
        "http://localhost:5173",
        "http://localhost:5174",
        "tauri://localhost",
        "http://tauri.localhost",
    ];

    public static IResourceBuilder<T> WithWebOrigins<T>(this IResourceBuilder<T> resource)
        where T : IResourceWithEnvironment
    {
        for (var i = 0; i < s_origins.Length; i++)
        {
            resource.WithEnvironment($"Cors__AllowedOrigins__{i}", s_origins[i]);
        }

        return resource;
    }
}

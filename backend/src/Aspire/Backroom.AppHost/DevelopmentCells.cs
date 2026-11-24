using Microsoft.Extensions.Configuration;

namespace Backroom.AppHost;

/// <summary>
/// A shared cell the AppHost runs. Its token audience is <c>cell-{Id}</c>; its database is <c>cell_{Id}</c>, its RabbitMQ
/// virtual host <c>cell-{Id}</c>, and its Valkey keys and channels carry the prefix <c>backroom:cell:{Id}:</c> (set by the
/// cell itself from its id).
/// </summary>
internal sealed record DevelopmentCell(string Id, string Name, string LaunchProfileSuffix)
{
    public string DatabaseName => "cell_" + Id;

    public string VirtualHost => "cell-" + Id;
}

/// <summary>Cells in development. Their ports are in the cell host's launch profiles (app 5200 and worker 5201 for a, 5202 and 5203 for b).</summary>
internal static class DevelopmentCells
{
    /// <summary>Configuration key (environment: <c>Backroom__Cells</c>) for how many cells to run, 1 or 2; default 2.</summary>
    public const string CountKey = "Backroom:Cells";

    public static readonly IReadOnlyList<DevelopmentCell> All =
    [
        new("a", "Development cell A", LaunchProfileSuffix: ""),
        new("b", "Development cell B", LaunchProfileSuffix: "-b"),
    ];

    /// <summary>The cells to run: all by default, only the first when <see cref="CountKey"/> is 1.</summary>
    public static IReadOnlyList<DevelopmentCell> Selected(IConfiguration configuration)
    {
        var configured = configuration[CountKey];
        if (string.IsNullOrEmpty(configured))
        {
            return All;
        }

        if (!int.TryParse(configured, System.Globalization.CultureInfo.InvariantCulture, out var count) || count < 1 || count > All.Count)
        {
            throw new InvalidOperationException($"{CountKey} must be a number from 1 to {All.Count}.");
        }

        return [.. All.Take(count)];
    }
}

namespace Backroom.AppHost;

/// <summary>
/// Where each development cell keeps its key and registration marker (<c>ControlPlaneConnector:KeyDirectory</c>). The
/// registration lives in the control plane's database and Keycloak, which are Docker volumes named after this AppHost
/// (its path hash), so the key directory carries the same id: another checkout or worktree of the repository gets
/// its own directories instead of a marker the other environment's control plane never saw. The cell's default
/// directory (<c>%APPDATA%/Backroom/cells/{cellId}</c>) is shared by every checkout and is not used here.
/// </summary>
/// <remarks>Pure, without Aspire types, so the unit tests compile this file directly.</remarks>
internal static class DevelopmentKeyDirectories
{
    /// <summary>The key directory of <paramref name="cellId"/> for the AppHost <paramref name="appHostId"/>.</summary>
    public static string For(string appDataRoot, string appHostId, string cellId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        return Path.Combine(appDataRoot, "Backroom", "dev", CheckSegment(appHostId), "cells", CheckSegment(cellId));
    }

    /// <summary>
    /// The AppHost's id from the name Aspire gives one of its volumes (<c>{application}-{path hash}-{resource}-{suffix}</c>,
    /// for example <c>backroom.apphost-24f7985b54-postgres-data</c>), so the directory changes exactly when the volumes do.
    /// </summary>
    public static string AppHostIdFromVolumeName(string volumeName, string resourceAndSuffix)
    {
        var tail = "-" + resourceAndSuffix;
        return volumeName.EndsWith(tail, StringComparison.Ordinal) && volumeName.Length > tail.Length
            ? CheckSegment(volumeName[..^tail.Length])
            : throw new ArgumentException($"The volume name does not end with '{tail}'.", nameof(volumeName));
    }

    // One path segment of letters, digits, '.', '-' or '_', never "." or "..".
    private static string CheckSegment(string segment) =>
        !string.IsNullOrEmpty(segment) && segment.Trim('.').Length > 0 && segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? segment
            : throw new ArgumentException("Not a safe directory name.", nameof(segment));
}

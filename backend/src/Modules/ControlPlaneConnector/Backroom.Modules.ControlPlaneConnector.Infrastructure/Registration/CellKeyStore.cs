using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Options;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Registration;

/// <summary>
/// The cell's EC P-256 key pair and its registration marker, in files only the cell's user can read.
/// The key is created and saved before registering, so a retried registration uses the same key; the
/// private key never leaves this store.
/// </summary>
internal sealed class CellKeyStore(IOptions<ControlPlaneConnectorOptions> options) : IDisposable
{
    private const string KeyFileName = "cell-key.pem";
    private const string RegisteredFileName = "registered";

    // The OID of the NIST P-256 curve (secp256r1).
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ECDsa? _key;

    public string Directory =>
        options.Value.KeyDirectory is { Length: > 0 } directory
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backroom", "cells", options.Value.CellId);

    private string KeyPath => Path.Combine(Directory, KeyFileName);

    private string RegisteredPath => Path.Combine(Directory, RegisteredFileName);

    public bool IsRegistered => File.Exists(RegisteredPath);

    /// <summary>The cell's key: loaded from its file, or created and saved on first use.</summary>
    public async Task<ECDsa> GetKeyAsync(CancellationToken cancellationToken)
    {
        if (_key is not null)
        {
            return _key;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_key is not null)
            {
                return _key;
            }

            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            if (File.Exists(KeyPath))
            {
                key.ImportFromPem(await File.ReadAllTextAsync(KeyPath, cancellationToken));
                if (key.ExportParameters(includePrivateParameters: false).Curve.Oid?.Value != P256Oid)
                {
                    key.Dispose();
                    throw new InvalidOperationException("The cell key file does not hold an EC P-256 key.");
                }
            }
            else
            {
                await WritePrivateFileAsync(KeyPath, key.ExportPkcs8PrivateKeyPem(), cancellationToken);
            }

            _key = key;
            return key;
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task MarkRegisteredAsync(string cellId, CancellationToken cancellationToken) =>
        WritePrivateFileAsync(RegisteredPath, cellId, cancellationToken);

    public void Dispose()
    {
        _key?.Dispose();
        _lock.Dispose();
    }

    // Written to a new temporary file first and moved into place, so a crash never leaves half a key.
    private async Task WritePrivateFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory();

        var temporary = Path.Combine(Directory, "." + Path.GetRandomFileName() + ".tmp");
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            await using (var stream = new FileStream(temporary, fileOptions))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    // Only the cell's user may use the directory, whether it was created here or configured.
    private void EnsurePrivateDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = System.IO.Directory.CreateDirectory(Directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            System.IO.Directory.CreateDirectory(Directory, OwnerOnly);
            File.SetUnixFileMode(Directory, OwnerOnly);
        }
    }
}

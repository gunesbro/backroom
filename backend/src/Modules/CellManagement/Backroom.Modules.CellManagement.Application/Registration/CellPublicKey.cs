using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>
/// A cell's public key: an EC P-256 JSON Web Key with the public part only. Anything else (other key
/// types or curves, a private component, malformed coordinates, a point not on the curve) is refused.
/// </summary>
internal sealed class CellPublicKey
{
    private const int CoordinateLength = 32;

    private static readonly HashSet<string> s_allowedMembers = new(StringComparer.Ordinal) { "kty", "crv", "x", "y", "kid", "use", "alg" };

    private CellPublicKey(string x, string y)
    {
        X = x;
        Y = y;
        Thumbprint = ComputeThumbprint(x, y);
    }

    public string X { get; }

    public string Y { get; }

    /// <summary>The RFC 7638 SHA-256 thumbprint (base64url); it identifies the key and is its <c>kid</c>.</summary>
    public string Thumbprint { get; }

    public static CellPublicKey? TryParse(JsonElement jwk)
    {
        if (jwk.ValueKind != JsonValueKind.Object
            || jwk.EnumerateObject().Any(member => !s_allowedMembers.Contains(member.Name))
            || !HasString(jwk, "kty", "EC")
            || !HasString(jwk, "crv", "P-256")
            || !TryCoordinate(jwk, "x", out var x)
            || !TryCoordinate(jwk, "y", out var y))
        {
            return null;
        }

        try
        {
            // Importing validates that the point lies on the curve.
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        }
        catch (Exception exception) when (exception is CryptographicException or PlatformNotSupportedException)
        {
            // Windows reports an invalid point as an unsupported curve.
            return null;
        }

        return new CellPublicKey(Base64Url.EncodeToString(x), Base64Url.EncodeToString(y));
    }

    /// <summary>The key as Keycloak stores it for signed-JWT client authentication.</summary>
    public string ToJwks() =>
        new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = X,
                ["y"] = Y,
                ["kid"] = Thumbprint,
                ["use"] = "sig",
                ["alg"] = "ES256",
            }),
        }.ToJsonString();

    private static string ComputeThumbprint(string x, string y)
    {
        // RFC 7638: the required members in lexicographic order, without whitespace.
        var canonical = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool HasString(JsonElement jwk, string name, string expected) =>
        jwk.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.ValueEquals(expected);

    private static bool TryCoordinate(JsonElement jwk, string name, out byte[] coordinate)
    {
        coordinate = [];
        if (!jwk.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString()!;
        if (text.Length != Base64Url.GetEncodedLength(CoordinateLength) || !Base64Url.IsValid(text))
        {
            return false;
        }

        coordinate = Base64Url.DecodeFromChars(text);
        return coordinate.Length == CoordinateLength;
    }
}

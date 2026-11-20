using System.Text.RegularExpressions;

namespace Backroom.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// The row-level security policy every organization table gets (docs/adr/0005).
/// </summary>
public static partial class OrganizationRowLevelSecurity
{
    public const string PolicyName = "org_isolation";

    public const string SettingName = "app.org_id";

    public const string OrganizationColumn = "org_id";

    /// <summary>
    /// Matches rows of the organization in <c>app.org_id</c>. Once a transaction-local setting has been
    /// used on a connection, PostgreSQL reports it as an empty string (not NULL) in later transactions;
    /// NULLIF turns that into NULL, which matches no row, instead of failing the uuid cast.
    /// </summary>
    public const string PolicyExpression =
        OrganizationColumn + " = NULLIF(current_setting('" + SettingName + "', true), '')::uuid";

    /// <summary>
    /// SQL that enables and forces row-level security on a table and adds the organization policy for
    /// every command. FORCE makes the policy apply to the table owner too.
    /// </summary>
    public static string EnableSql(string schema, string table)
    {
        var name = QualifiedName(schema, table);
        return $"""
            ALTER TABLE {name} ENABLE ROW LEVEL SECURITY;
            ALTER TABLE {name} FORCE ROW LEVEL SECURITY;
            CREATE POLICY {PolicyName} ON {name} FOR ALL USING ({PolicyExpression}) WITH CHECK ({PolicyExpression});
            """;
    }

    /// <summary>A quoted <c>"schema"."table"</c> name; only lower-case snake_case identifiers are accepted.</summary>
    public static string QualifiedName(string schema, string table)
    {
        ValidateIdentifier(schema, nameof(schema));
        ValidateIdentifier(table, nameof(table));
        return $"\"{schema}\".\"{table}\"";
    }

    /// <summary>Throws unless <paramref name="identifier"/> is a lower-case snake_case PostgreSQL identifier.</summary>
    public static void ValidateIdentifier(string identifier, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(identifier, parameterName);
        if (!IdentifierPattern().IsMatch(identifier))
        {
            throw new ArgumentException($"'{identifier}' is not a lower-case snake_case identifier.", parameterName);
        }
    }

    // PostgreSQL truncates identifiers longer than 63 bytes.
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}

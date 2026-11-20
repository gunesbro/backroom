using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Backroom.Modules.Directory.Infrastructure.Migrations;

/// <summary>
/// The control plane's invitations (docs/contracts/phase-1.md, "Data"): only the code's hash and the email's HMAC,
/// never either in clear. A revocation of an unknown invitation leaves a tombstone without them, so a late
/// registration cannot recreate it. Like every control-plane table it has no row-level security; queries always
/// name the organization.
/// </summary>
[DbContext(typeof(DirectoryDbContext))]
[Migration("20261115120000_CreateInvitations")]
internal sealed class CreateInvitations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE directory.invitations (
                invitation_id uuid PRIMARY KEY,
                org_id uuid NOT NULL REFERENCES directory.organizations (id),
                cell_id text NOT NULL,
                code_hash bytea CHECK (octet_length(code_hash) = 32),
                email_hmac bytea CHECK (octet_length(email_hmac) = 32),
                role text CHECK (role IN ('admin', 'member')),
                expires_at timestamptz,
                status text NOT NULL CHECK (status IN ('pending', 'accepting', 'used', 'failed', 'revoked', 'expired')),
                code_version integer CHECK (code_version > 0),
                accepting_account_id uuid,
                accepted_at timestamptz,
                failure_code text,
                revoked_at timestamptz,
                revoke_requested_at timestamptz,
                used_at timestamptz,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                -- While an invitation can still be accepted it has all its data. Afterwards the retention job
                -- deletes the email HMAC, and a revocation tombstone never had any of it.
                CHECK (status NOT IN ('pending', 'accepting')
                       OR (code_hash IS NOT NULL AND email_hmac IS NOT NULL AND role IS NOT NULL
                           AND expires_at IS NOT NULL AND code_version IS NOT NULL)),
                CHECK (status NOT IN ('accepting', 'used') OR (accepting_account_id IS NOT NULL AND accepted_at IS NOT NULL)),
                CHECK (status <> 'failed' OR failure_code IS NOT NULL));

            -- Resolving and accepting look an invitation up by its code; a hash belongs to one invitation.
            CREATE UNIQUE INDEX invitations_code_hash_idx ON directory.invitations (code_hash) WHERE code_hash IS NOT NULL;

            -- An organization's invitations (deletion, repair).
            CREATE INDEX invitations_org_id_idx ON directory.invitations (org_id);
            """);
    }
}

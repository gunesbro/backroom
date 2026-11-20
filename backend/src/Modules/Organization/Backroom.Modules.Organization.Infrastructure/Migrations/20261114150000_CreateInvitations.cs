using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Backroom.BuildingBlocks.Infrastructure.Messaging;
using Backroom.BuildingBlocks.Infrastructure.Persistence;

namespace Backroom.Modules.Organization.Infrastructure.Migrations;

/// <summary>
/// Invitations (docs/contracts/phase-1.md, "Data") and the module's outbox, both under row-level security. Only
/// <c>SHA-256(code)</c> is stored. The email becomes NULL when the retention job deletes the personal data of
/// an expired or revoked invitation.
/// </summary>
[DbContext(typeof(OrganizationDbContext))]
[Migration("20261114150000_CreateInvitations")]
internal sealed class CreateInvitations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("""
            CREATE TABLE organization.invitations (
                id uuid PRIMARY KEY,
                org_id uuid NOT NULL REFERENCES organization.organizations (id),
                email text CHECK (char_length(email) BETWEEN 3 AND 254),
                role text NOT NULL CHECK (char_length(role) BETWEEN 1 AND 64),
                salt bytea NOT NULL CHECK (octet_length(salt) = 16),
                key_version integer NOT NULL CHECK (key_version > 0),
                code_version integer NOT NULL CHECK (code_version > 0),
                code_hash bytea NOT NULL CHECK (octet_length(code_hash) = 32),
                status text NOT NULL CHECK (status IN ('pending', 'accepted', 'revoked', 'expired')),
                created_by uuid NOT NULL,
                created_at timestamptz NOT NULL,
                expires_at timestamptz NOT NULL,
                registered_at timestamptz,
                accepted_at timestamptz,
                accepted_by uuid,
                revoked_at timestamptz,
                revoked_by uuid,
                CHECK (status <> 'pending' OR email IS NOT NULL));

            -- At most one pending invitation per address; a new one revokes the old one first.
            CREATE UNIQUE INDEX invitations_pending_email_idx ON organization.invitations (org_id, email) WHERE status = 'pending';

            -- The list of pending invitations (keyset by id) and their count.
            CREATE INDEX invitations_pending_idx ON organization.invitations (org_id, id) WHERE status = 'pending';

            -- The daily limits count an organization's invitations of the last 24 hours.
            CREATE INDEX invitations_created_at_idx ON organization.invitations (org_id, created_at);

            -- Whether an invited address already belongs to a member. Invited addresses are ASCII (punycode
            -- domains) in lower case; COLLATE "C" lowers ASCII letters only, whatever the database's collation
            -- (a Turkish collation would turn 'I' into 'ı'). A generated column, so existing rows are filled
            -- although row-level security hides them from this migration.
            ALTER TABLE organization.members
                ADD COLUMN email_key text GENERATED ALWAYS AS (lower(email COLLATE "C")) STORED;
            CREATE INDEX members_email_key_idx ON organization.members (org_id, email_key);
            """);
        migrationBuilder.EnableOrganizationRowLevelSecurity(OrganizationDbContext.Schema, "invitations");
        migrationBuilder.CreateOutboxTable(OrganizationDbContext.Schema);
    }
}

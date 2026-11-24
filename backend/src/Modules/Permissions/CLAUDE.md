# Permissions (cell)

Contract: [docs/contracts/phase-1.md](../../../../docs/contracts/phase-1.md). **Human review required** for every change in this module.

## Responsibility

- System roles per organization: Owner (all permissions, locked), Admin and Member (defaults from the requirements' permission list).
- `IPermissionsProvisioning` (Permissions.Contracts), called synchronously by Organization inside its transaction: `InitializeOrganizationAsync(orgId)` creates the system roles; `AssignRoleAsync(orgId, accountId, roleKey)`; `RemoveMemberAsync(orgId, accountId)`. Writes only this module's tables.
- `IPermissionChecker.HasPermissionAsync(accountId, permission)` (Permissions.Contracts), in the organization of the current unit of work, and the endpoint filter `RequirePermission("channels.create-public")` (BuildingBlocks.Presentation, through `IOrganizationPermissionCheck`, so other modules need no reference here).
- Phase 1 permission keys: `channels.create-public`, `members.invite-by-email`, `members.choose-role-on-invite`, `members.manage-invitations` (Owner, Admin). The rest arrive with the role screens in Phase 5.
- Phase 2 keys so far: `files.upload` (Owner, Admin, Member) and `files.remove-others-public` (Owner, Admin); existing organizations get them from the migration `AddFilePermissions` (through `OrganizationDataSql`).
- `IPermissionChecker.CanGrantRoleAsync(accountId, roleKey)`: true only if the role's permissions are a subset of the account's and the role is not Owner (used for invitations).

## Owns

- Schema `permissions`: `roles`, `role_permissions`, `member_roles`.

## Rules

- There is no permission that allows reading or exporting other people's messages, and none may be added.
- Owner's permissions cannot be edited; nobody assigns a role higher than their own or Owner through invitations.
- Checks are cached per request only; a role change takes effect on the next request.

## Status

- Implemented: schema `permissions` with `roles`, `role_permissions` and `member_roles` (each with `org_id`, under RLS; composite foreign keys `(org_id, role_id)` keep a row and its role in one organization even though foreign-key checks ignore RLS); `IPermissionsProvisioning.InitializeOrganizationAsync` (Owner, Admin, Member from `SystemRoles`, the contract's Phase 1 table) and `AssignRoleAsync`, both idempotent. No Application or Presentation project yet.
- `IPermissionChecker` (`HasPermissionAsync`, `CanGrantRoleAsync`) and `RequirePermission`: a member without the permission gets `403 permissions.forbidden`; outsiders get the membership check's 404. The checker treats Owner as having every permission, so a key added later needs no backfill. An unknown key throws: it is a programming error, not a denial.
- `IPermissionsProvisioning.RemoveMemberAsync` deletes every `member_roles` row of the account in the organization (in the caller's unit of work, under RLS); Organization calls it before a removed member who rejoins by invitation gets the invited role, so no earlier role (Admin, Owner) comes back. Member removal must call it too once it exists.

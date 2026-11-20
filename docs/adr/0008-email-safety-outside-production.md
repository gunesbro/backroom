# 0008. Email never leaves non-production environments

- Status: Accepted
- Date: 2026-10-25

## Context

The product sends invitations, new-device alerts, reminders and calendar invites. A misconfigured development or test environment must never send real email to real people. Organizations can configure their own SMTP server, which is another path for accidental sending.

## Decision

1. In every non-production environment, email always goes to Mailpit. This is hard-coded, not configurable; an organization's own SMTP setting is ignored there.
2. Real sending requires two switches: the environment is Production and an explicit "allow real sending" setting is on.
3. Developer machines never hold credentials for a real email provider.
4. At startup outside production, the application refuses to start if the SMTP target is not Mailpit.
5. If Mailpit is down, email waits in the queue or fails; it is never redirected to another server.
6. Mailpit's relay feature is never configured.
7. Keycloak's development SMTP settings also point to Mailpit.
8. Test data uses `.test` addresses; tests read email through the Mailpit API. Architecture tests verify that only the email module uses SMTP.

Mailpit image versions are pinned, its UI listens on localhost only (password-protected on any shared server), stored messages are capped, and CI starts a clean Mailpit for each run.

## Consequences

- The same approach applies to other outbound channels: outside production, webhooks, calendar invites and notifications reach only test accounts or fake recipients.
- Code touching these safeguards requires human review.
- In production, a delivery log (template and version, recipient, organization and person, provider message ID, status history, no content) is kept for 90 days, and a per-cell block list stops sending to bounced or complaining addresses.

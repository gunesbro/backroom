// Public surface of the invitations feature: keeping an invitation link's code and joining its organization.
import { lazy } from 'react';

export { extractInvitationCode } from './invitation-code';
export {
  captureInvitationCode,
  clearInvitationCode,
  hasPendingInvitation,
  saveInvitationCode,
  sweepInvitationCode,
} from './pending-invitation';

/** The invite page, loaded only when an invitation is open. */
export const InvitePage = lazy(() =>
  import('./invite-page').then(({ InvitePage: page }) => ({ default: page })),
);

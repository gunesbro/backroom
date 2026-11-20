// Imported first by main.tsx, so it runs before any other module: an invitation link's code leaves the address
// bar before a router, monitoring or session recording could see it (docs/contracts/phase-1.md, "Transport").
import { captureInvitationCode, sweepInvitationCode } from '@/features/invitations';

sweepInvitationCode();
captureInvitationCode();

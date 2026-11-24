import { useAuth } from '@/features/auth';
import { clearInvitationCode } from '@/features/invitations';

/** Signs out; a kept invitation code does not outlive the person's session. */
export function useSignOut() {
  const { signOut } = useAuth();
  return () => {
    clearInvitationCode();
    void signOut();
  };
}

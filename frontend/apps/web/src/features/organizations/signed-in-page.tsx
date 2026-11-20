import type { ReactNode } from 'react';
import { Account, AppPage, Brand, LinkButton } from '@backroom/ui';
import { config } from '@/config';
import { useAuth } from '@/features/auth';
import { clearInvitationCode } from '@/features/invitations';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';

/** The frame of every signed-in screen (docs/design: Onboarding): product, the account and sign-out on top. */
export function SignedInPage({ children }: { children: ReactNode }) {
  const t = useTranslate(messages);
  const { user, signOut } = useAuth();
  return (
    <AppPage
      start={<Brand name={config.productName} />}
      end={
        <Account name={user.name}>
          <LinkButton
            onClick={() => {
              // A kept invitation code does not outlive the person's session.
              clearInvitationCode();
              void signOut();
            }}
          >
            {t('signOut')}
          </LinkButton>
        </Account>
      }
    >
      {children}
    </AppPage>
  );
}

import type { ReactNode } from 'react';
import { Account, AppPage, Brand, LinkButton } from '@backroom/ui';
import { config } from '@/config';
import { useAuth } from '@/features/auth';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';
import { useSignOut } from './sign-out';

/** The frame of every signed-in screen (docs/design: Onboarding): product, the account and sign-out on top. */
export function SignedInPage({ children }: { children: ReactNode }) {
  const t = useTranslate(messages);
  const { user } = useAuth();
  const signOut = useSignOut();
  return (
    <AppPage
      start={<Brand name={config.productName} />}
      end={
        <Account name={user.name}>
          <LinkButton onClick={signOut}>{t('signOut')}</LinkButton>
        </Account>
      }
    >
      {children}
    </AppPage>
  );
}

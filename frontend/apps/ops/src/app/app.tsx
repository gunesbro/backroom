// Operations panel for our own team (cells, organizations, releases). Phase 1 has the cell list.
// It never shows customer content: only counts and health.
import { useEffect, useMemo, useState } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { resolveLocale, type Locale, type Messages } from '@backroom/i18n';
import {
  Account,
  Alert,
  AppPage,
  Brand,
  Button,
  Card,
  Heading,
  LinkButton,
  Page,
  Spinner,
  Status,
} from '@backroom/ui';
import type { AuthClient, AuthUser } from '@/auth/auth-client';
import { CellList } from '@/cells/cell-list';
import { translator } from '@/shared/locale';

const PRODUCT_NAME = 'Backroom Operations';

type AppKey = 'signingIn' | 'signInFailed' | 'tryAgain' | 'signOut';

const messages: Messages<AppKey> = {
  en: {
    signingIn: 'Signing you in…',
    signInFailed: 'Sign-in failed',
    tryAgain: 'Try again',
    signOut: 'Sign out',
  },
  tr: {
    signingIn: 'Oturum açılıyor…',
    signInFailed: 'Oturum açılamadı',
    tryAgain: 'Tekrar dene',
    signOut: 'Çıkış yap',
  },
};

type State = { status: 'signing-in' } | { status: 'failed' } | { status: 'signed-in'; user: AuthUser };

function createQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { staleTime: 0, gcTime: 5 * 60_000 } } });
}

export function App({ auth, locale: forced }: { auth: AuthClient; locale?: Locale }) {
  const locale = forced ?? resolveLocale(navigator.languages);
  const t = translator(messages, locale);
  const [state, setState] = useState<State>({ status: 'signing-in' });
  const [queryClient] = useState(createQueryClient);
  const getAccessToken = useMemo(() => () => auth.getAccessToken(), [auth]);

  useEffect(() => {
    let mounted = true;
    auth.start().then(
      (user) => {
        if (mounted) setState({ status: 'signed-in', user });
      },
      () => {
        if (mounted) setState({ status: 'failed' });
      },
    );
    return () => {
      mounted = false;
    };
  }, [auth]);

  if (state.status === 'signed-in') {
    return (
      <QueryClientProvider client={queryClient}>
        <AppPage
          start={<Brand name={PRODUCT_NAME} />}
          end={
            <Account name={state.user.name}>
              <LinkButton
                onClick={() => {
                  auth.signOut().catch(() => undefined);
                }}
              >
                {t('signOut')}
              </LinkButton>
            </Account>
          }
        >
          <CellList getAccessToken={getAccessToken} locale={locale} />
        </AppPage>
      </QueryClientProvider>
    );
  }

  return (
    <Page>
      <main>
        <Card narrow>
          <Brand name={PRODUCT_NAME} />
          {state.status === 'failed' ? (
            <>
              <Alert announce={false}>
                <Heading>{t('signInFailed')}</Heading>
              </Alert>
              <Button
                variant="primary"
                onClick={() => {
                  auth.signIn().catch(() => undefined);
                }}
              >
                {t('tryAgain')}
              </Button>
            </>
          ) : (
            <Status>
              <Spinner />
              <Heading>{t('signingIn')}</Heading>
            </Status>
          )}
        </Card>
      </main>
    </Page>
  );
}

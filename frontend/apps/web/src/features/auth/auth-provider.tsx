import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { Alert, Brand, Button, Card, Heading, Page, Spinner, Status } from '@backroom/ui';
import { config } from '@/config';
import { useLocale, useTranslate } from '@/shared/locale';
import { SignInRequiredError, type AuthClient, type AuthUser } from './auth-client';
import { AuthContext, type Auth } from './auth-context';
import { DesktopLogin, DesktopSignInStart } from './desktop-login';
import { messages } from './messages';

type State =
  | { status: 'signing-in' }
  /** Desktop: no session; the person starts the browser sign-in. */
  | { status: 'ready' }
  | { status: 'in-browser' }
  | { status: 'failed' }
  | { status: 'signed-in'; user: AuthUser };

/** Shows its children only to a signed-in account. */
export function AuthProvider({ client, children }: { client: AuthClient; children: ReactNode }) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const [state, setState] = useState<State>({ status: 'signing-in' });
  const mounted = useRef(true);
  // Read when a sign-in starts; a language change must not restart the sign-in check.
  const localeRef = useRef(locale);
  useEffect(() => {
    localeRef.current = locale;
  }, [locale]);

  // Desktop: the sign-in runs in the system browser while this screen waits.
  const signInInBrowser = useCallback(() => {
    const browser = client.browserSignIn;
    if (!browser) return;
    setState({ status: 'in-browser' });
    browser
      .run(localeRef.current)
      .then(() => client.start())
      .then(
        (user) => {
          if (mounted.current) setState({ status: 'signed-in', user });
        },
        (error: unknown) => {
          if (!mounted.current) return;
          // The person cancelled: back to the start screen.
          setState(error === 'cancelled' ? { status: 'ready' } : { status: 'failed' });
        },
      );
  }, [client]);

  useEffect(() => {
    mounted.current = true;
    client.start().then(
      (user) => {
        if (mounted.current) setState({ status: 'signed-in', user });
      },
      (error: unknown) => {
        if (!mounted.current) return;
        setState(
          error instanceof SignInRequiredError && client.browserSignIn
            ? { status: 'ready' }
            : { status: 'failed' },
        );
      },
    );
    return () => {
      mounted.current = false;
    };
  }, [client]);

  const auth = useMemo<Auth | null>(
    () =>
      state.status === 'signed-in'
        ? {
            user: state.user,
            getAccessToken: () => client.getAccessToken(),
            // If the sign-in server cannot be reached the person stays signed in and can try again.
            signOut: () => client.signOut().catch(() => undefined),
          }
        : null,
    [client, state],
  );

  if (auth) {
    return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
  }

  const browser = client.browserSignIn;
  if (state.status === 'ready' && browser) {
    return <DesktopSignInStart onStart={signInInBrowser} />;
  }

  if (state.status === 'in-browser' && browser) {
    return (
      <DesktopLogin
        signIn={browser}
        onCancel={() => {
          browser.cancel().catch(() => undefined);
        }}
      />
    );
  }

  return (
    <Page>
      <Card narrow>
        <Brand name={config.productName} />
        {state.status === 'failed' ? (
          <>
            <Alert announce={false}>
              <Heading>{t('signInFailed')}</Heading>
            </Alert>
            <Button
              variant="primary"
              onClick={() => {
                if (browser) {
                  signInInBrowser();
                } else {
                  client.signIn().catch(() => undefined);
                }
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
    </Page>
  );
}

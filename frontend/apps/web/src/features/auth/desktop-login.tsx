import { useState } from 'react';
import { Brand, Button, Card, Heading, LinkButton, Muted, Page, Spinner, Status } from '@backroom/ui';
import { config } from '@/config';
import { useTranslate } from '@/shared/locale';
import type { BrowserSignIn } from './auth-client';
import { messages } from './messages';

/**
 * The desktop app before sign-in: the person starts it, so the browser never opens unexpectedly (at start or
 * after signing out).
 */
export function DesktopSignInStart({ onStart }: { onStart: () => void }) {
  const t = useTranslate(messages);

  return (
    <Page>
      <Card narrow>
        <Brand name={config.productName} />
        <Heading>{t('desktopSignIn')}</Heading>
        <Muted>{t('desktopSignInText')}</Muted>
        <Button variant="primary" onClick={onStart}>
          {t('signInWithBrowser')}
        </Button>
      </Card>
    </Page>
  );
}

type CopyState = 'idle' | 'copied' | 'failed';

/** The desktop app while the sign-in runs in the system browser (docs/design DesktopLogin, screen 1). */
export function DesktopLogin({ signIn, onCancel }: { signIn: BrowserSignIn; onCancel: () => void }) {
  const t = useTranslate(messages);
  const [copy, setCopy] = useState<CopyState>('idle');

  const copyLink = async () => {
    // The link is written in the same click; WebKit drops the permission once the click's task has ended.
    const text = signIn.link().then((link) => {
      if (!link) throw new Error('No sign-in is waiting.');
      return link;
    });
    if (typeof ClipboardItem === 'function') {
      await navigator.clipboard.write([
        new ClipboardItem({ 'text/plain': text.then((link) => new Blob([link], { type: 'text/plain' })) }),
      ]);
    } else {
      await navigator.clipboard.writeText(await text);
    }
  };

  return (
    <Page>
      <Card narrow>
        <Brand name={config.productName} />
        <Heading>{t('signInInBrowser')}</Heading>
        <Muted>{t('signInInBrowserText')}</Muted>
        <Status>
          <Spinner />
          {t('waitingForBrowser')}
        </Status>
        <Button
          onClick={() => {
            signIn.reopen().catch(() => undefined);
          }}
        >
          {t('reopenBrowser')}
        </Button>
        <LinkButton
          onClick={() => {
            copyLink().then(
              () => {
                setCopy('copied');
              },
              () => {
                setCopy('failed');
              },
            );
          }}
        >
          {t('copySignInLink')}
        </LinkButton>
        {copy !== 'idle' && (
          <p role="status" className="ph-muted">
            {t(copy === 'copied' ? 'signInLinkCopied' : 'signInLinkCopyFailed')}
          </p>
        )}
        <LinkButton onClick={onCancel}>{t('cancelSignIn')}</LinkButton>
      </Card>
    </Page>
  );
}

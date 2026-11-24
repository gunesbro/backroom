import { useId } from 'react';
import { Button, Heading, LinkButton, Muted } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { useAuth } from './auth-context';
import { messages } from './messages';

/**
 * Desktop: the organization's server needs a scope this sign-in lacks. The person starts the browser sign-in here;
 * nothing opens by itself. `onDismiss` runs after Cancel, so the view can show the server as unavailable.
 */
export function CellSignInPrompt({ onDismiss }: { onDismiss: () => void }) {
  const t = useTranslate(messages);
  const { cellSignIn } = useAuth();
  const titleId = useId();
  if (!cellSignIn) return null;

  return (
    <div className="bm-stack" role="region" aria-labelledby={titleId}>
      <Heading id={titleId} level={2}>
        {t('cellSignIn')}
      </Heading>
      <Muted>{t('cellSignInText')}</Muted>
      <Button variant="primary" onClick={cellSignIn.start}>
        {t('signInWithBrowser')}
      </Button>
      <LinkButton
        onClick={() => {
          cellSignIn.dismiss();
          onDismiss();
        }}
      >
        {t('cancelSignIn')}
      </LinkButton>
    </div>
  );
}

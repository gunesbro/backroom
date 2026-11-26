import { useMutation } from '@tanstack/react-query';
import { Alert, Button } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';

/**
 * Where the composer would be for a public channel the person reads without having joined it
 * (docs/contracts/phase-2.md, flow 1: every member can read a public channel). Joining is the only action.
 */
export function PreviewBar({ join, onJoined }: { join: () => Promise<void>; onJoined: () => void }) {
  const t = useTranslate(messages);
  const mutation = useMutation({ mutationFn: join, onSuccess: onJoined });

  return (
    <div className="bm-preview">
      <p className="bm-preview__text">{t('previewNotice')}</p>
      <Button
        variant="primary"
        disabled={mutation.isPending}
        onClick={() => {
          mutation.mutate();
        }}
      >
        {t('joinPreviewed')}
      </Button>
      {mutation.isError && <Alert>{t('generic')}</Alert>}
    </div>
  );
}

import { useRef } from 'react';
import { Button, Dialog } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import { messages } from './messages';

/**
 * Asks before a message is deleted for everyone. The safe button (Cancel) takes the focus, so Enter right after the
 * dialog opens does not delete anything; Escape and the close button cancel too.
 */
export function DeleteMessageDialog({
  onConfirm,
  onCancel,
}: {
  onConfirm: () => void;
  onCancel: () => void;
}) {
  const t = useTranslate(messages);
  const cancelRef = useRef<HTMLButtonElement>(null);
  return (
    <Dialog title={t('deleteTitle')} closeLabel={t('close')} onClose={onCancel} initialFocus={cancelRef}>
      <div className="ph-dialog__body">
        <p>{t('deleteBody')}</p>
      </div>
      <div className="ph-dialog__foot">
        <button ref={cancelRef} type="button" className="ph-button" onClick={onCancel}>
          {t('cancel')}
        </button>
        <Button className="bm-delete" onClick={onConfirm}>
          {t('deleteConfirm')}
        </Button>
      </div>
    </Dialog>
  );
}

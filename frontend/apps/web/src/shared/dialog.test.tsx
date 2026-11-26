import { createEvent, fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { Dialog } from '@backroom/ui';
import { EmojiPicker } from '@/features/emoji';
import { LocaleContext } from '@/shared/locale';

function renderDialog(onClose = vi.fn<() => void>()) {
  render(
    <Dialog title="Title" closeLabel="Close" onClose={onClose}>
      <button type="button">Inside</button>
    </Dialog>,
  );
  return { onClose, dialog: screen.getByRole('dialog', { name: 'Title' }) };
}

describe('Dialog', () => {
  it('Dialog_WhenRendered_IsANativeDialogOpenedAsModal', () => {
    const { dialog } = renderDialog();
    expect(dialog.tagName).toBe('DIALOG');
    expect(dialog).toHaveAttribute('open');
    expect(dialog.dataset.modal).toBe('true');
    expect(dialog).not.toHaveAttribute('aria-modal');
  });

  it('Dialog_WhenEscapeIsPressed_CallsOnCloseOnce', () => {
    const { dialog, onClose } = renderDialog();
    const event = createEvent.keyDown(dialog, { key: 'Escape' });
    fireEvent(dialog, event);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it('Dialog_WhenTheBrowserRequestsCancel_CallsOnCloseAndPreventsIt', () => {
    const { dialog, onClose } = renderDialog();
    const event = new Event('cancel', { cancelable: true });
    fireEvent(dialog, event);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(event.defaultPrevented).toBe(true);
  });

  it('Dialog_WhenUnmounted_ClosesTheNativeDialog', () => {
    const close = vi.spyOn(HTMLDialogElement.prototype, 'close');
    const view = render(
      <Dialog title="Title" closeLabel="Close" onClose={() => undefined}>
        x
      </Dialog>,
    );
    view.unmount();
    expect(close).toHaveBeenCalledTimes(1);
    close.mockRestore();
  });

  it('Dialog_WhenEscapeClearsTheEmojiSearch_StaysOpen', async () => {
    const onClose = vi.fn<() => void>();
    render(
      <LocaleContext.Provider value="en">
        <Dialog title="Title" closeLabel="Close" onClose={onClose}>
          <EmojiPicker onPick={() => undefined} />
        </Dialog>
      </LocaleContext.Provider>,
    );
    const box = await screen.findByRole('searchbox', { name: 'Search emoji' });
    fireEvent.change(box, { target: { value: 'smile' } });
    const event = createEvent.keyDown(box, { key: 'Escape' });
    fireEvent(box, event);
    expect(box).toHaveValue('');
    expect(event.defaultPrevented).toBe(true);
    expect(onClose).not.toHaveBeenCalled();
  });
});

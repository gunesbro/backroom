import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import { Composer } from './composer';

describe('Composer with the rich text flag off', () => {
  it('Send_WhenFlagIsOff_PassesTextOnlyAndNoBlocks', () => {
    const onSend = vi.fn();
    render(
      <LocaleProvider>
        <Composer channelName="general" onSend={onSend} />
      </LocaleProvider>,
    );

    const input = screen.getByRole('textbox', { name: 'Message #general' });
    fireEvent.change(input, { target: { value: 'hello' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    expect(onSend).toHaveBeenCalledExactlyOnceWith('hello');
  });
});

import { act, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import type { Message } from './api';
import type { Row } from './message-list';
import { ANNOUNCE_EVERY_MS, NewMessageAnnouncer } from './new-message-announcer';

const OWN = 'own';
const message = (seq: number, authorAccountId = 'veli'): Row => {
  const value: Message = {
    id: `m${String(seq)}`,
    seq,
    clientMessageId: `c${String(seq)}`,
    authorAccountId,
    text: `text ${String(seq)}`,
    createdAt: '2026-11-22T10:00:00Z',
  };
  return { key: value.id, message: value };
};

function setup(initial: Row[], active = true) {
  const authorName = (id: string) => (id === 'veli' ? 'Veli' : 'Member');
  const ui = (rows: Row[], isActive = active) => (
    <LocaleProvider locale="en">
      <NewMessageAnnouncer rows={rows} ownAccountId={OWN} authorName={authorName} active={isActive} />
    </LocaleProvider>
  );
  const view = render(ui(initial));
  return {
    update: (rows: Row[], isActive = active) => {
      view.rerender(ui(rows, isActive));
    },
  };
}

const live = () => screen.getByRole('status');

afterEach(() => {
  vi.useRealTimers();
});

describe('NewMessageAnnouncer', () => {
  it('Announce_MessagesPresentOnOpening_AreNotRead', () => {
    vi.useFakeTimers();
    setup([message(1), message(2)]);

    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS * 2);
    });

    expect(live()).toBeEmptyDOMElement();
    expect(live()).toHaveAttribute('aria-live', 'polite');
  });

  it('Announce_ANewMessageFromAnotherPerson_IsReadOnceAfterTheThrottle', () => {
    vi.useFakeTimers();
    const { update } = setup([message(1)]);

    update([message(1), message(2)]);
    expect(live()).toBeEmptyDOMElement();
    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS);
    });

    expect(live()).toHaveTextContent('Veli: text 2.');
  });

  it('Announce_OwnMessagesAndEarlierHistory_AreNotRead', () => {
    vi.useFakeTimers();
    const { update } = setup([message(10)]);

    update([message(5), message(10), message(11, OWN)]);
    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS);
    });

    expect(live()).toBeEmptyDOMElement();
  });

  it('Announce_ManyAtOnce_ReadsOnlyTheNewestThree', () => {
    vi.useFakeTimers();
    const { update } = setup([message(1)]);

    update([1, 2, 3, 4, 5, 6].map((seq) => message(seq)));
    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS);
    });

    expect(live()).toHaveTextContent('Veli: text 4. Veli: text 5. Veli: text 6.');
  });

  it('Announce_WhileReadingHistory_MarksNewerPagesSeenWithoutReadingThem', () => {
    vi.useFakeTimers();
    const { update } = setup([message(1)]);

    update([message(1), message(2), message(3)], false);
    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS);
    });
    update([message(1), message(2), message(3)], true);
    act(() => {
      vi.advanceTimersByTime(ANNOUNCE_EVERY_MS);
    });

    expect(live()).toBeEmptyDOMElement();
  });
});

import { act, fireEvent, render, screen, within } from '@testing-library/react';
import axe from 'axe-core';
import { beforeAll, describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import type { Message } from './api';
import { MessageList, type Row } from './message-list';
import { rowKey } from './row-key';

beforeAll(() => {
  // jsdom has no layout: the feed is 400 px tall, a row 40 px, and scrollTop can be set.
  const tops = new WeakMap<Element, number>();
  const isFeed = (element: Element) => element.getAttribute('role') === 'feed';
  Object.defineProperties(HTMLElement.prototype, {
    offsetHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isFeed(this) ? 400 : 40;
      },
    },
    offsetWidth: { configurable: true, get: () => 600 },
    clientHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isFeed(this) ? 400 : 0;
      },
    },
    scrollHeight: {
      configurable: true,
      get: function (this: HTMLElement) {
        return isFeed(this) ? Number.parseInt(this.querySelector('ol')?.style.height ?? '0', 10) : 0;
      },
    },
    scrollTo: {
      configurable: true,
      value: function (this: HTMLElement, options: { top?: number }) {
        tops.set(this, options.top ?? 0);
      },
    },
    scrollTop: {
      configurable: true,
      get: function (this: HTMLElement) {
        return tops.get(this) ?? 0;
      },
      set: function (this: HTMLElement, value: number) {
        tops.set(this, value);
      },
    },
  });
});

const OTHER = '00000000-0000-4000-8000-000000000002';
const message = (seq: number): Message => ({
  id: `m-${String(seq)}`,
  seq,
  clientMessageId: `c-${String(seq)}`,
  authorAccountId: OTHER,
  text: `message ${String(seq)}`,
  createdAt: '2026-11-22T10:00:00Z',
});
const rowOf = (seq: number): Row => ({ key: `c-${String(seq)}`, message: message(seq) });
const range = (from: number, to: number): Row[] =>
  Array.from({ length: to - from + 1 }, (_, index) => rowOf(from + index));

function list(rows: readonly Row[]) {
  return (
    <LocaleProvider locale="en">
      <MessageList
        channelName="general"
        rows={rows}
        hasEarlier={false}
        loadingEarlier={false}
        onLoadEarlier={vi.fn()}
        hasNewer={false}
        loadingNewer={false}
        onLoadNewer={vi.fn()}
        authorName={() => 'Veli'}
        ownAccountId={undefined}
        ownName="Alice"
        locale="en"
        onRetry={vi.fn()}
        onDiscard={vi.fn()}
        onJoin={vi.fn()}
      />
    </LocaleProvider>
  );
}

const article = (text: string) =>
  screen.getByText(text).closest<HTMLElement>('[role="article"]') as HTMLElement;
const focusOn = (element: HTMLElement) => {
  act(() => {
    element.focus();
  });
};
const bars = () => screen.queryAllByRole('button', { name: 'Reply' });

describe('Message feed focus and the action bar', () => {
  it('ActionBar_OnTouchTap_TogglesEvenThoughTheTapFocusesTheRow', () => {
    render(list(range(1, 3)));
    const row = article('message 3');

    // A tap: press, the row takes focus, release.
    fireEvent.pointerDown(row, { pointerType: 'touch' });
    focusOn(row);
    fireEvent.pointerUp(row, { pointerType: 'touch' });
    expect(bars()).toHaveLength(1);
    fireEvent.pointerDown(row, { pointerType: 'touch' });
    fireEvent.pointerUp(row, { pointerType: 'touch' });
    expect(bars()).toHaveLength(0);
  });

  it('ActionBar_OnKeyboardFocus_Shows_AndAPressFocusDoesNotPinItOpen', () => {
    render(list(range(1, 3)));
    focusOn(article('message 2'));
    expect(bars()).toHaveLength(1);

    const other = article('message 1');
    fireEvent.pointerDown(other, { pointerType: 'mouse' });
    focusOn(other);
    fireEvent.pointerUp(other, { pointerType: 'mouse' });

    // The pointer is not over the row in this test: only keyboard focus keeps a bar open.
    expect(bars()).toHaveLength(0);
  });

  it('ActionBar_TapOnAButtonInTheRow_DoesNotToggle', () => {
    render(list(range(1, 3)));
    fireEvent.pointerUp(article('message 3'), { pointerType: 'touch' });

    fireEvent.pointerUp(bars()[0] as HTMLElement, { pointerType: 'touch' });

    expect(bars()).toHaveLength(1);
  });

  it('Escape_OutsideTheActionBar_IsLeftAlone', () => {
    render(list(range(1, 3)));
    const row = article('message 3');
    focusOn(row);

    expect(fireEvent.keyDown(row, { key: 'Escape' })).toBe(true);
    expect(row).toHaveFocus();
  });

  it('PageKeys_MoveBetweenArticles', () => {
    render(list(range(1, 3)));
    focusOn(article('message 2'));

    fireEvent.keyDown(article('message 2'), { key: 'PageDown' });
    expect(article('message 3')).toHaveFocus();
    fireEvent.keyDown(article('message 3'), { key: 'PageUp' });
    expect(article('message 2')).toHaveFocus();
  });

  it('FocusedRow_WhenScrolledOutOfTheDrawnRange_StaysInThePage', () => {
    render(list(range(1, 60)));
    const feed = screen.getByRole('feed');
    act(() => {
      fireEvent.scroll(feed);
    });
    const last = article('message 60');
    focusOn(last);

    act(() => {
      feed.scrollTop = 0;
      fireEvent.scroll(feed);
    });

    expect(screen.queryByText('message 30')).not.toBeInTheDocument();
    expect(article('message 60')).toBe(last);
    expect(last).toHaveFocus();
  });

  it('FocusedRow_WhenEarlierPagesLoadAbove_KeepsItsElementAndFocus', () => {
    const { rerender } = render(list(range(21, 40)));
    const row = article('message 30');
    focusOn(row);

    rerender(list(range(1, 40)));

    expect(article('message 30')).toBe(row);
    expect(row).toHaveFocus();
  });

  it('FocusedRow_WhenAPendingMessageIsConfirmed_KeepsItsElementAndFocus', () => {
    const pending: Row = {
      key: 'c-9',
      pending: {
        clientMessageId: 'c-9',
        text: 'message 9',
        createdAt: '2026-11-22T10:00:00Z',
        status: 'sending',
      },
    };
    const { rerender } = render(list([...range(1, 3), pending]));
    const row = article('message 9');
    focusOn(row);

    rerender(list([...range(1, 3), rowOf(9)]));

    expect(article('message 9')).toBe(row);
    expect(row).toHaveFocus();
  });

  it('FocusedRow_WhenItsPageIsDropped_MovesFocusToTheNearestRow', () => {
    const { rerender } = render(list(range(1, 10)));
    focusOn(article('message 5'));

    rerender(list(range(7, 10)));

    expect(document.activeElement).toBe(article('message 7'));
  });

  it('FocusedRow_AfterArrowMoves_WhenDropped_MovesFocusToTheRowNextToTheCurrentPosition', () => {
    const { rerender } = render(list(range(1, 10)));
    focusOn(article('message 2'));
    fireEvent.keyDown(article('message 2'), { key: 'ArrowDown' });
    fireEvent.keyDown(article('message 3'), { key: 'ArrowDown' });
    fireEvent.keyDown(article('message 4'), { key: 'ArrowDown' });
    expect(article('message 5')).toHaveFocus();

    rerender(list([...range(1, 4), ...range(6, 10)]));

    // The row now at the old position (message 6), which is already drawn.
    expect(document.activeElement).toBe(article('message 6'));
  });

  it('Rows_OfTwoSendersWithTheSameClientMessageId_GetDistinctKeys', () => {
    const mine = rowKey({ authorAccountId: 'a', clientMessageId: 'c-1', id: 'm-1' });
    const theirs = rowKey({ authorAccountId: 'b', clientMessageId: 'c-1', id: 'm-2' });

    expect(mine).not.toBe(theirs);
    expect(rowKey({ authorAccountId: 'a', clientMessageId: '', id: 'm-3' })).toBe('m-3');
  });

  it('Feed_WithTheBarShown_HasNoAxeViolations', async () => {
    const { container } = render(list(range(1, 5)));
    focusOn(article('message 5'));

    const results = await axe.run(container, {
      rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
    });

    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
    expect(within(screen.getByRole('feed')).getAllByRole('article')).toHaveLength(5);
  });
});

describe('A send refused because the person has not joined', () => {
  it('FailedSend_NotAMember_OffersJoinAndRetriesTheSameMessage', () => {
    const onJoin = vi.fn();
    const failed: Row = {
      key: 'p-1',
      pending: {
        clientMessageId: 'client-1',
        text: 'hello',
        createdAt: '2026-11-24T10:00:00Z',
        status: 'failed',
        code: 'messaging.not_a_member',
      },
    };
    render(
      <LocaleProvider locale="en">
        <MessageList
          channelName="general"
          rows={[failed]}
          hasEarlier={false}
          loadingEarlier={false}
          onLoadEarlier={vi.fn()}
          hasNewer={false}
          loadingNewer={false}
          onLoadNewer={vi.fn()}
          authorName={() => 'Veli'}
          ownAccountId={undefined}
          ownName="Alice"
          locale="en"
          onRetry={vi.fn()}
          onDiscard={vi.fn()}
          onJoin={onJoin}
        />
      </LocaleProvider>,
    );

    expect(screen.getByText(/Join this channel to post in it\./)).toBeTruthy();
    expect(screen.queryByText("Couldn't send.")).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Join' }));
    expect(onJoin).toHaveBeenCalledWith('client-1');
  });
});

function failedRow(code: string, reason?: string): Row {
  return {
    key: 'p-1',
    pending: {
      clientMessageId: 'client-1',
      text: 'hello',
      createdAt: '2026-11-24T10:00:00Z',
      status: 'failed',
      code,
      reason,
    },
  };
}

function renderFailed(row: Row, locale: 'en' | 'tr') {
  const onRetry = vi.fn();
  render(
    <LocaleProvider locale={locale}>
      <MessageList
        channelName="general"
        rows={[row]}
        hasEarlier={false}
        loadingEarlier={false}
        onLoadEarlier={vi.fn()}
        hasNewer={false}
        loadingNewer={false}
        onLoadNewer={vi.fn()}
        authorName={() => 'Veli'}
        ownAccountId={undefined}
        ownName="Alice"
        locale={locale}
        onRetry={onRetry}
        onDiscard={vi.fn()}
        onJoin={vi.fn()}
      />
    </LocaleProvider>,
  );
  return onRetry;
}

describe('A refused rich text send', () => {
  it('FailedSend_WhenLinksLimit_ShowsTheEnglishTextAndNoRetry', () => {
    renderFailed(failedRow('messaging.message_invalid', 'links.limit'), 'en');

    expect(screen.getByText(/too many different links/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Discard' })).toBeTruthy();
  });

  it('FailedSend_WhenLinksLimit_ShowsTheTurkishText', () => {
    renderFailed(failedRow('messaging.message_invalid', 'links.limit'), 'tr');

    expect(screen.getByText(/çok fazla farklı bağlantı/)).toBeTruthy();
  });

  it('FailedSend_WhenTransportFails_OffersRetry', () => {
    const onRetry = renderFailed(failedRow('http.502'), 'en');

    fireEvent.click(screen.getByRole('button', { name: 'Try again' }));

    expect(onRetry).toHaveBeenCalledWith('client-1');
  });
});

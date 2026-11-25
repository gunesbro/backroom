import { render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { installEmojiCopy } from './copy';
import { EmojiText } from './emoji-image';

/** Dispatches a copy on `target`; returns what the page put on the clipboard and whether it took the copy over. */
function copy(target: Element) {
  const data = new Map<string, string>();
  const event = new Event('copy', { bubbles: true, cancelable: true });
  Object.defineProperty(event, 'clipboardData', {
    value: {
      setData: (type: string, value: string) => {
        data.set(type, value);
      },
    },
  });
  target.dispatchEvent(event);
  return { data, handled: event.defaultPrevented };
}

function select(start: Node, startOffset: number, end: Node, endOffset: number) {
  const range = document.createRange();
  range.setStart(start, startOffset);
  range.setEnd(end, endOffset);
  const selection = document.getSelection();
  selection?.removeAllRanges();
  selection?.addRange(range);
}

let uninstall: (() => void) | undefined;
afterEach(() => {
  uninstall?.();
  uninstall = undefined;
  document.getSelection()?.removeAllRanges();
});

function renderMessages() {
  uninstall = installEmojiCopy();
  const { container } = render(
    <div>
      <p id="first">
        <EmojiText text="Well done 🎉 team 👍🏽" />
      </p>
      <p id="second">
        <EmojiText text="No emoji here" />
      </p>
    </div>,
  );
  const list = container.firstElementChild as HTMLElement;
  return {
    list,
    first: list.querySelector('#first') as HTMLElement,
    second: list.querySelector('#second') as HTMLElement,
  };
}

describe('Copying emoji', () => {
  it('Copy_OfTextWithEmojiImages_PutsTheCharactersOnTheClipboard', () => {
    const { first } = renderMessages();
    select(first, 0, first, first.childNodes.length);

    const { data, handled } = copy(first);

    expect(handled).toBe(true);
    expect(data.get('text/plain')).toBe('Well done 🎉 team 👍🏽');
    // The formatted copy carries characters too: no image and no address of ours.
    expect(data.get('text/html')).toBe('Well done 🎉 team 👍🏽');
    expect([...data.keys()]).toEqual(['text/plain', 'text/html']);
  });

  it('Copy_OfAPartOfTheText_TakesOnlyTheSelectedEmoji', () => {
    const { first } = renderMessages();
    // From "done" to just after the first emoji.
    select(first.firstChild as Text, 5, first, 2);

    expect(copy(first).data.get('text/plain')).toBe('done 🎉');
  });

  it('Copy_AcrossMessages_KeepsTheirMarkupInTheFormattedCopy', () => {
    const { list } = renderMessages();
    select(list, 0, list, 2);

    const { data } = copy(list);

    expect(data.get('text/html')).toBe(
      '<p id="first">Well done 🎉 team 👍🏽</p><p id="second">No emoji here</p>',
    );
    expect(data.get('text/plain')).toContain('team 👍🏽');
  });

  it('Copy_WithoutEmojiImagesOrWithoutASelection_IsLeftToTheBrowser', () => {
    const { second } = renderMessages();

    expect(copy(second).handled).toBe(false);
    select(second, 0, second, 1);
    const { data, handled } = copy(second);
    expect(handled).toBe(false);
    expect(data.size).toBe(0);
  });

  it('Copy_InsideAnEditorOrATextField_IsLeftToIt', () => {
    const { first } = renderMessages();
    first.setAttribute('contenteditable', 'true');
    select(first, 0, first, first.childNodes.length);

    expect(copy(first).handled).toBe(false);
  });

  it('Copy_AfterUninstalling_IsLeftToTheBrowser', () => {
    const { first } = renderMessages();
    select(first, 0, first, first.childNodes.length);
    uninstall?.();

    expect(copy(first).handled).toBe(false);
  });
});

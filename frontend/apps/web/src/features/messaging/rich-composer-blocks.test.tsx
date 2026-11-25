// The composer's block editing through the real component: keys and toolbar buttons, as a person uses them. The
// editing rules themselves are tested on the editor state in rich-text/editing.test.ts.
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { $getRoot, $getSelection, $isRangeSelection, type LexicalEditor } from 'lexical';
import { describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import { Composer } from './composer';
import type { Block, TextNode } from './rich-text/blocks';
import { $readBlocks, $writeBlocks } from './rich-text/lexical-blocks';

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return { config: { ...original.config, richTextEnabled: true } };
});

type WithEditor = HTMLElement & { __lexicalEditor?: LexicalEditor };

function text(value: string): TextNode {
  return { type: 'text', text: value };
}

function paragraphs(...lines: string[]): Block[] {
  return lines.map((line) => ({ type: 'paragraph', children: [text(line)] }));
}

function list(type: 'bulleted_list' | 'numbered_list', ...items: string[]): Block {
  return { type, children: items.map((item) => ({ type: 'list_item', children: [text(item)] })) };
}

async function setup(initial: Block[] = []) {
  const onSend = vi.fn<(text: string, blocks?: Block[]) => void>();
  const view = render(
    <LocaleProvider>
      <Composer channelName="general" onSend={onSend} />
    </LocaleProvider>,
  );
  const input = await screen.findByRole<WithEditor>('textbox', { name: 'Message #general' });
  const editor = input.__lexicalEditor as LexicalEditor;
  const run = (action: () => void) => {
    act(() => {
      editor.update(action, { discrete: true });
    });
  };
  /** Puts the caret into the text node with this text (at its end or an offset), or into the first block. */
  const caret = (value?: string, offset?: number) => {
    run(() => {
      if (value === undefined) {
        $getRoot().selectStart();
        return;
      }
      const node = $getRoot()
        .getAllTextNodes()
        .find((candidate) => candidate.getTextContent() === value);
      if (!node) throw new Error(`no text node "${value}"`);
      const at = offset ?? node.getTextContentSize();
      node.select(at, at);
    });
  };
  /** Types characters one by one at the caret. */
  const type = (value: string) => {
    for (const character of value) {
      run(() => {
        const selection = $getSelection();
        if ($isRangeSelection(selection)) selection.insertText(character);
      });
    }
  };
  const press = (key: string, shiftKey = false) => {
    let notPrevented = true;
    act(() => {
      notPrevented = fireEvent.keyDown(input, { key, shiftKey });
    });
    return notPrevented;
  };
  run(() => {
    $writeBlocks(initial);
  });
  return { ...view, onSend, input, caret, type, press, blocks: () => editor.read($readBlocks) };
}

describe('Rich text composer lines', () => {
  it('Composer_WhenShiftEnterPressedInAParagraph_StartsANewParagraphAndDoesNotSend', async () => {
    const { onSend, caret, press, blocks, input } = await setup(paragraphs('hello'));
    caret('hello', 2);

    const notPrevented = press('Enter', true);

    expect(notPrevented).toBe(false);
    expect(onSend).not.toHaveBeenCalled();
    expect(blocks()).toEqual(paragraphs('he', 'llo'));
    expect(input.querySelectorAll('p.bm-rt-line')).toHaveLength(2);
  });

  it.each<[string, Block[], string]>([
    ['list', [list('bulleted_list', 'one', 'two')], 'two'],
    ['quote', [{ type: 'quote', children: [text('said\nso')] }], 'so'],
    ['code block', [{ type: 'code_block', children: [text('a = 1\nb = 2')] }], 'b = 2'],
  ])('Composer_WhenEnterPressedInA%s_SendsIt', async (_name, initial, last) => {
    const { onSend, caret, press } = await setup(initial);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Send' })).toBeEnabled();
    });
    caret(last);

    press('Enter');

    expect(onSend).toHaveBeenCalledOnce();
    expect(onSend.mock.calls[0]?.[1]).toEqual(initial);
  });

  it('Composer_WhenShiftEnterPressedInAList_AddsAnItemAndOnTheEmptyItemLeavesTheList', async () => {
    const { caret, type, press, blocks } = await setup([list('numbered_list', 'one')]);
    caret('one');

    press('Enter', true);
    type('two');
    expect(blocks()).toEqual([list('numbered_list', 'one', 'two')]);

    press('Enter', true);
    press('Enter', true);
    type('after');
    expect(blocks()).toEqual([list('numbered_list', 'one', 'two'), ...paragraphs('after')]);
  });

  it('Composer_WhenShiftEnterPressedInAQuoteAndACodeBlock_StaysInsideUntilTheLineIsEmpty', async () => {
    const { caret, type, press, blocks } = await setup([
      { type: 'quote', children: [text('q1')] },
      { type: 'code_block', children: [text('c1')] },
    ]);
    caret('q1');
    press('Enter', true);
    type('q2');
    caret('c1');
    press('Enter', true);
    type('c2');
    expect(blocks()).toEqual([
      { type: 'quote', children: [text('q1\nq2')] },
      { type: 'code_block', children: [text('c1\nc2')] },
    ]);

    press('Enter', true);
    press('Enter', true);
    type('out');
    expect(blocks()).toEqual([
      { type: 'quote', children: [text('q1\nq2')] },
      { type: 'code_block', children: [text('c1\nc2')] },
      ...paragraphs('out'),
    ]);
  });

  it('Composer_WhenBackspacePressedBeforeTheFirstCharacterOfAnItem_UnlistsOnlyThatItem', async () => {
    const { caret, press, blocks } = await setup([list('bulleted_list', 'a', 'b', 'c')]);
    caret('b', 0);

    const notPrevented = press('Backspace');

    expect(notPrevented).toBe(false);
    expect(blocks()).toEqual([list('bulleted_list', 'a'), ...paragraphs('b'), list('bulleted_list', 'c')]);
  });

  it('Composer_WhenArrowDownPressedOnTheLastLineOfATrailingCodeBlock_MovesBelowIt', async () => {
    const { caret, type, press, blocks } = await setup([{ type: 'code_block', children: [text('x')] }]);
    caret('x');

    press('ArrowDown');
    type('below');

    expect(blocks()).toEqual([{ type: 'code_block', children: [text('x')] }, ...paragraphs('below')]);
  });
});

describe('Rich text composer block buttons', () => {
  it('Composer_WhenQuotePressedWithTheCaretOnOneLine_QuotesOnlyThatLineAndTogglesOff', async () => {
    const { caret, blocks } = await setup(paragraphs('a', 'b', 'c'));
    caret('b');

    fireEvent.click(screen.getByRole('button', { name: 'Quote' }));
    expect(blocks()).toEqual([
      ...paragraphs('a'),
      { type: 'quote', children: [text('b')] },
      ...paragraphs('c'),
    ]);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quote' })).toHaveAttribute('aria-pressed', 'true');
    });

    fireEvent.click(screen.getByRole('button', { name: 'Quote' }));
    expect(blocks()).toEqual(paragraphs('a', 'b', 'c'));
  });

  it('Composer_WhenCodeBlockPressedWithTheCaretOnOneLine_BoxesOnlyThatLine', async () => {
    const { caret, blocks, input } = await setup(paragraphs('a', 'b', 'c'));
    caret('b');

    fireEvent.click(screen.getByRole('button', { name: 'Code block' }));

    expect(blocks()).toEqual([
      ...paragraphs('a'),
      { type: 'code_block', children: [text('b')] },
      ...paragraphs('c'),
    ]);
    expect(input.querySelectorAll('.bm-code-block')).toHaveLength(1);
  });

  it('Composer_WhenListPressedOnOneItem_TurnsTheListOffForThatItemOnly', async () => {
    const { caret, blocks, input } = await setup([list('bulleted_list', 'a', 'b')]);
    expect(input.querySelectorAll('ul.bm-rt-list > li')).toHaveLength(2);
    caret('b');

    fireEvent.click(screen.getByRole('button', { name: 'Bulleted list' }));

    expect(blocks()).toEqual([list('bulleted_list', 'a'), ...paragraphs('b')]);
  });

  it.each<[string, Block['type']]>([
    ['- ', 'bulleted_list'],
    ['* ', 'bulleted_list'],
    ['1. ', 'numbered_list'],
    ['> ', 'quote'],
    ['```', 'code_block'],
  ])('Composer_When%sTypedAtTheStartOfALine_StartsThatBlock', async (shortcut, expected) => {
    const { caret, type, blocks } = await setup();
    caret();

    type(shortcut);
    type('x');

    expect(blocks().map((block) => block.type)).toEqual([expected]);
  });

  it('Composer_WhenABlockIsStartedWithoutText_HidesThePlaceholderAndKeepsSendDisabled', async () => {
    const { caret, container } = await setup();
    expect(container.querySelector('.bm-rt__placeholder')).toHaveTextContent('Message #general');
    caret();

    fireEvent.click(screen.getByRole('button', { name: 'Numbered list' }));

    await waitFor(() => {
      expect(container.querySelector('.bm-rt__placeholder')).toBeNull();
    });
    expect(screen.getByRole('button', { name: 'Send' })).toBeDisabled();
  });
});

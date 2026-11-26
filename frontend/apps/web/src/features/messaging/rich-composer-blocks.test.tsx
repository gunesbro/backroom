// The composer's block editing through the real component: keys and toolbar buttons, as a person uses them. The
// editing rules themselves are tested on the editor state in rich-text/editing.test.ts.
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import {
  $getRoot,
  $getSelection,
  $isRangeSelection,
  INSERT_PARAGRAPH_COMMAND,
  KEY_ENTER_COMMAND,
  PASTE_COMMAND,
  REDO_COMMAND,
  UNDO_COMMAND,
  type LexicalCommand,
  type LexicalEditor,
} from 'lexical';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
import { Composer } from './composer';
import type { Block, TextNode } from './rich-text/blocks';
import { $readBlocks, $writeBlocks } from './rich-text/lexical-blocks';

vi.mock('@/config', async (importOriginal) => {
  const original = await importOriginal<typeof import('@/config')>();
  return { config: { ...original.config, richTextEnabled: true } };
});

// jsdom has no layout: after an undo the editor asks where the caret is, to scroll it into view.
Range.prototype.getBoundingClientRect = () => ({
  x: 0,
  y: 0,
  top: 0,
  left: 0,
  right: 0,
  bottom: 0,
  width: 0,
  height: 0,
  toJSON: () => ({}),
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
  const dispatch = <T,>(command: LexicalCommand<T>, payload: T) => {
    act(() => {
      editor.dispatchCommand(command, payload);
    });
  };
  return { ...view, onSend, input, caret, type, press, dispatch, blocks: () => editor.read($readBlocks) };
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

  it('Composer_WhenEnterConfirmsCompositionAfterCompositionEnd_NeitherSendsNorAddsALine', async () => {
    const { onSend, caret, blocks, input } = await setup(paragraphs('hello'));
    caret('hello', 2);

    // WebKit delivers compositionend first, so isComposing is false and only keyCode 229 marks the key.
    act(() => {
      fireEvent.keyDown(input, { key: 'Enter', keyCode: 229 });
    });

    expect(onSend).not.toHaveBeenCalled();
    expect(blocks()).toEqual(paragraphs('hello'));
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

describe('Rich text composer undo', () => {
  it('Composer_WhenABlockToggleIsUndoneAndRedone_RestoresBothStates', async () => {
    const { caret, dispatch, blocks } = await setup(paragraphs('a', 'b', 'c'));
    caret('b');
    fireEvent.click(screen.getByRole('button', { name: 'Quote' }));
    const quoted = [...paragraphs('a'), { type: 'quote', children: [text('b')] }, ...paragraphs('c')];
    expect(blocks()).toEqual(quoted);

    dispatch(UNDO_COMMAND, undefined);
    expect(blocks()).toEqual(paragraphs('a', 'b', 'c'));

    dispatch(REDO_COMMAND, undefined);
    expect(blocks()).toEqual(quoted);
  });

  it('Composer_WhenATypedCodeFenceIsUndoneAndRedone_TakesTheBlockAwayAndBringsItBack', async () => {
    const { caret, type, dispatch, blocks } = await setup(paragraphs('intro'));
    caret('intro');
    type('```');
    type('x');
    const fenced = [...paragraphs('intro'), { type: 'code_block', children: [text('x')] }];
    expect(blocks()).toEqual(fenced);

    dispatch(UNDO_COMMAND, undefined);
    dispatch(UNDO_COMMAND, undefined);
    expect(blocks().map((block) => block.type)).toEqual(['paragraph']);

    dispatch(REDO_COMMAND, undefined);
    dispatch(REDO_COMMAND, undefined);
    expect(blocks()).toEqual(fenced);
  });
});

describe('Rich text composer Enter without a keyboard', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('Composer_WhenEnterArrivesWithoutAKeyEvent_MakesALineAndDoesNotSend', async () => {
    const { onSend, caret, type, dispatch, blocks } = await setup([
      { type: 'quote', children: [text('q1')] },
    ]);
    caret('q1');

    dispatch(KEY_ENTER_COMMAND, null);
    type('q2');
    dispatch(INSERT_PARAGRAPH_COMMAND, undefined);
    type('q3');

    expect(onSend).not.toHaveBeenCalled();
    expect(blocks()).toEqual([{ type: 'quote', children: [text('q1\nq2\nq3')] }]);
  });

  it('Composer_WhenEnterPressedOnATouchDevice_MakesALineAndDoesNotSend', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('pointer: coarse') }));
    const { onSend, caret, press, blocks } = await setup(paragraphs('hello'));
    caret('hello', 2);

    press('Enter');

    expect(onSend).not.toHaveBeenCalled();
    expect(blocks()).toEqual(paragraphs('he', 'llo'));
  });
});

describe('Rich text composer arrows and paste', () => {
  it('Composer_WhenArrowDownPressedWithAModifierOrTwice_AddsAtMostOneParagraph', async () => {
    const { caret, input, press } = await setup([{ type: 'code_block', children: [text('x')] }]);
    caret('x');

    for (const modifier of ['ctrlKey', 'altKey', 'metaKey', 'shiftKey']) {
      expect(fireEvent.keyDown(input, { key: 'ArrowDown', [modifier]: true }), modifier).toBe(true);
    }
    expect(input.querySelectorAll('p')).toHaveLength(0);

    expect(press('ArrowDown')).toBe(false);
    expect(press('ArrowDown')).toBe(true);
    caret('x');
    expect(press('ArrowDown')).toBe(true);
    expect(input.querySelectorAll('p')).toHaveLength(1);
  });

  it('Composer_WhenLinesArePastedIntoAQuote_KeepsThemQuoted', async () => {
    const { caret, dispatch, blocks } = await setup([{ type: 'quote', children: [text('said: ')] }]);
    caret('said: ');
    const paste = Object.assign(new Event('paste', { cancelable: true }), {
      clipboardData: { getData: (type: string) => (type === 'text/plain' ? 'one\ntwo' : '') },
    });

    dispatch(PASTE_COMMAND, paste as unknown as ClipboardEvent);

    expect(paste.defaultPrevented).toBe(true);
    expect(blocks()).toEqual([{ type: 'quote', children: [text('said: one\ntwo')] }]);
  });
});

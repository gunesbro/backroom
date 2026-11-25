import { $createListItemNode, $createListNode, ListNode, registerList } from '@lexical/list';
import { registerRichText } from '@lexical/rich-text';
import {
  $createRangeSelection,
  $createTextNode,
  $getRoot,
  $getSelection,
  $isRangeSelection,
  $setSelection,
  TextNode,
  createEditor,
  type TextNode as LexicalText,
} from 'lexical';
import { describe, expect, it } from 'vitest';
import type { Block, TextNode as Run } from './blocks';
import {
  $codeFenceFromText,
  $flattenNestedList,
  $insertLine,
  $isPristine,
  $leaveCodeBlock,
  $toggleBlock,
  $unformatAtLineStart,
} from './editing';
import { $readBlocks, $writeBlocks, EDITOR_NODES } from './lexical-blocks';

const MARK_ORDER = ['bold', 'italic', 'strike', 'code', 'link'];

/** What the server would refuse (docs/contracts/phase-2.md, "Block schema (exact)"), as far as the composer can err. */
function schemaProblems(blocks: readonly Block[]): string[] {
  const problems: string[] = [];
  const runs = (children: readonly Run[], where: string, marksAllowed: boolean) => {
    for (const [index, run] of children.entries()) {
      if (Object.keys(run).some((key) => !['type', 'text', 'marks'].includes(key)))
        problems.push(`${where}.unknown_member`);
      if (run.text.length === 0) problems.push('text.text');
      if (run.text.includes('\r')) problems.push('text.text');
      if (run.marks) {
        const order = run.marks.map((mark) => MARK_ORDER.indexOf(mark.type));
        if (run.marks.length === 0 || order.some((value, at) => at > 0 && value <= (order[at - 1] ?? -1)))
          problems.push('text.marks');
        if (!marksAllowed) problems.push('code_block.marks');
      }
      const previous = children[index - 1];
      if (previous && JSON.stringify(previous.marks ?? []) === JSON.stringify(run.marks ?? []))
        problems.push('text.not_merged');
    }
  };
  const blank = (block: Block | undefined) =>
    block?.type === 'paragraph' && block.children.every((run) => run.text.trim().length === 0);
  if (blocks.length === 0) problems.push('blocks.empty');
  if (blank(blocks[0]) || blank(blocks[blocks.length - 1])) problems.push('paragraph.blank_edge');
  for (const block of blocks) {
    if (block.type === 'bulleted_list' || block.type === 'numbered_list') {
      if (block.children.length === 0) problems.push('list.empty');
      for (const item of block.children) {
        if (item.children.length === 0) problems.push('list_item.empty');
        runs(item.children, 'list_item', true);
      }
    } else {
      if (block.type !== 'paragraph' && block.children.length === 0) problems.push(`${block.type}.empty`);
      runs(block.children, block.type, block.type !== 'code_block');
    }
  }
  return problems;
}

function text(value: string): Run {
  return { type: 'text', text: value };
}

function paragraphs(...lines: string[]): Block[] {
  return lines.map((line) => ({ type: 'paragraph', children: [text(line)] }));
}

function list(type: 'bulleted_list' | 'numbered_list', ...items: string[]): Block {
  return { type, children: items.map((item) => ({ type: 'list_item', children: [text(item)] })) };
}

function setup(initial: Block[] = []) {
  const editor = createEditor({
    namespace: 'test',
    nodes: [...EDITOR_NODES],
    onError: (error) => {
      throw error;
    },
  });
  registerRichText(editor);
  registerList(editor);
  editor.registerNodeTransform(TextNode, $codeFenceFromText);
  editor.registerNodeTransform(ListNode, $flattenNestedList);
  const run = (action: () => void) => {
    editor.update(action, { discrete: true });
  };
  const $find = (value: string): LexicalText => {
    const node = $getRoot()
      .getAllTextNodes()
      .find((candidate) => candidate.getTextContent() === value);
    if (!node) throw new Error(`no text node "${value}"`);
    return node;
  };
  run(() => {
    $writeBlocks(initial);
  });
  return {
    run,
    /** The blocks that would be sent; they are always checked against the schema. */
    blocks: () => {
      const blocks = editor.read($readBlocks);
      if (blocks.length > 0) expect(schemaProblems(blocks)).toEqual([]);
      return blocks;
    },
    /** The editor's top-level nodes as `type:text`. */
    nodes: () =>
      editor.read(() =>
        $getRoot()
          .getChildren()
          .map((node) => `${node.getType()}:${node.getTextContent()}`),
      ),
    /** Puts the caret into the text node with this text, at its end or at an offset. */
    caret: (value: string, offset?: number) => {
      run(() => {
        const node = $find(value);
        const at = offset ?? node.getTextContentSize();
        node.select(at, at);
      });
    },
    /** Puts the caret into the first empty block. */
    caretInEmpty: () => {
      run(() => {
        const node = $getRoot()
          .getChildren()
          .find((child) => child.getTextContentSize() === 0);
        if (!node) throw new Error('no empty block');
        node.selectStart();
      });
    },
    /** Selects from the start of one text node to the end (or an offset) of another. */
    select: (from: string, to: string, toOffset?: number) => {
      run(() => {
        const first = $find(from);
        const last = $find(to);
        const selection = $createRangeSelection();
        selection.anchor.set(first.getKey(), 0, 'text');
        selection.focus.set(last.getKey(), toOffset ?? last.getTextContentSize(), 'text');
        $setSelection(selection);
      });
    },
    /** Types characters one by one at the caret, like a keyboard does. */
    type: (value: string) => {
      for (const character of value) {
        run(() => {
          const selection = $getSelection();
          if ($isRangeSelection(selection)) selection.insertText(character);
        });
      }
    },
    caretText: () =>
      editor.read(() => {
        const selection = $getSelection();
        return $isRangeSelection(selection) ? selection.anchor.getNode().getTextContent() : null;
      }),
    read: <T>(reader: () => T) => editor.read(reader),
  };
}

describe('Line-scoped quote', () => {
  it('ToggleQuote_WhenCaretIsOnOneOfThreeLines_QuotesOnlyThatLine', () => {
    const { run, caret, blocks, caretText } = setup(paragraphs('a', 'b', 'c'));
    caret('b', 0);
    run(() => {
      $toggleBlock('quote');
    });
    expect(blocks()).toEqual([
      ...paragraphs('a'),
      { type: 'quote', children: [text('b')] },
      ...paragraphs('c'),
    ]);
    expect(caretText()).toBe('b');
  });

  it('ToggleQuote_WhenSelectionCoversTwoLines_SendsThemAsOneQuote', () => {
    const { run, select, blocks, nodes } = setup(paragraphs('a', 'b', 'c'));
    select('a', 'b');
    run(() => {
      $toggleBlock('quote');
    });
    expect(nodes()).toEqual(['quote:a', 'quote:b', 'paragraph:c']);
    expect(blocks()).toEqual([{ type: 'quote', children: [text('a\nb')] }, ...paragraphs('c')]);
  });

  it('ToggleQuote_WhenCaretIsOnAQuotedLine_UnquotesOnlyThatLine', () => {
    const { run, caret, blocks } = setup([{ type: 'quote', children: [text('a\nb\nc')] }]);
    caret('b');
    run(() => {
      $toggleBlock('quote');
    });
    expect(blocks()).toEqual([
      { type: 'quote', children: [text('a')] },
      ...paragraphs('b'),
      { type: 'quote', children: [text('c')] },
    ]);
  });

  it('ToggleQuote_WhenSelectionEndsBeforeTheFirstCharacterOfALine_LeavesThatLineAlone', () => {
    const { run, select, blocks } = setup(paragraphs('a', 'b', 'c'));
    select('a', 'b', 0);
    run(() => {
      $toggleBlock('quote');
    });
    expect(blocks()).toEqual([{ type: 'quote', children: [text('a')] }, ...paragraphs('b', 'c')]);
  });

  it('Read_WhenQuotedLinesCarryMarks_KeepsThemAcrossTheLineBreak', () => {
    const bold = (value: string): Run => ({ type: 'text', text: value, marks: [{ type: 'bold' }] });
    const quote: Block[] = [{ type: 'quote', children: [bold('a\nb'), text('\nc')] }];
    const { blocks, nodes } = setup(quote);
    expect(nodes()).toEqual(['quote:a', 'quote:b', 'quote:c']);
    expect(blocks()).toEqual(quote);
  });
});

describe('Line-scoped code block', () => {
  it('ToggleCode_WhenSelectionCoversTwoOfThreeLines_MakesOneCodeBlockOfThem', () => {
    const { run, select, blocks } = setup(paragraphs('a', 'b', 'c'));
    select('a', 'b');
    run(() => {
      $toggleBlock('code');
    });
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a\nb')] }, ...paragraphs('c')]);
  });

  it('ToggleCode_WhenCaretIsOnTheMiddleLineOfACodeBlock_TakesOnlyThatLineOut', () => {
    const { run, caret, blocks } = setup([{ type: 'code_block', children: [text('a\nb\nc')] }]);
    caret('b');
    run(() => {
      $toggleBlock('code');
    });
    expect(blocks()).toEqual([
      { type: 'code_block', children: [text('a')] },
      ...paragraphs('b'),
      { type: 'code_block', children: [text('c')] },
    ]);
  });

  it('ToggleCode_WhenLineNextToACodeBlock_JoinsTheBlock', () => {
    const { run, caret, blocks } = setup([
      { type: 'code_block', children: [text('a')] },
      ...paragraphs('b'),
      { type: 'code_block', children: [text('c')] },
    ]);
    caret('b');
    run(() => {
      $toggleBlock('code');
    });
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a\nb\nc')] }]);
  });

  it('ToggleCode_WhenTextHasMarksAndALink_KeepsOnlyTheCharacters', () => {
    const { run, caret, blocks } = setup([
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'bold', marks: [{ type: 'bold' }] },
          text(' and '),
          { type: 'text', text: 'site', marks: [{ type: 'link', href: 'https://example.com/' }] },
        ],
      },
    ]);
    caret('bold');
    run(() => {
      $toggleBlock('code');
    });
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('bold and site')] }]);
  });

  it('ToggleCode_WhenCoveringQuoteListAndParagraph_MakesOneBlockWithALinePerLine', () => {
    const { run, select, blocks } = setup([
      { type: 'quote', children: [text('q')] },
      list('bulleted_list', 'one', 'two'),
      ...paragraphs('p'),
    ]);
    select('q', 'p');
    run(() => {
      $toggleBlock('code');
    });
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('q\none\ntwo\np')] }]);
  });

  it('Read_WhenCodeBlockHasEmptyLinesAround_SendsOnlyTheCodeAndKeepsItsIndentation', () => {
    const { blocks } = setup([{ type: 'code_block', children: [text('\n  a\n\n  b\n\n')] }]);
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('  a\n\n  b')] }]);
  });
});

describe('Lists', () => {
  it('ToggleList_WhenSelectionCoversLines_MakesOneItemPerLine', () => {
    const { run, select, blocks } = setup(paragraphs('a', 'b', 'c'));
    select('a', 'b');
    run(() => {
      $toggleBlock('number');
    });
    expect(blocks()).toEqual([list('numbered_list', 'a', 'b'), ...paragraphs('c')]);
  });

  it('ToggleList_WhenCaretIsOnOneItem_TurnsOnlyThatItemOffAndOnAgain', () => {
    const { run, caret, blocks } = setup([list('bulleted_list', 'a', 'b', 'c')]);
    caret('b');
    run(() => {
      $toggleBlock('bullet');
    });
    expect(blocks()).toEqual([list('bulleted_list', 'a'), ...paragraphs('b'), list('bulleted_list', 'c')]);

    run(() => {
      $toggleBlock('bullet');
    });
    expect(blocks()).toEqual([list('bulleted_list', 'a', 'b', 'c')]);
  });

  it('ToggleList_WhenItemIsOfTheOtherKind_ChangesOnlyThatItem', () => {
    const { run, caret, blocks } = setup([list('bulleted_list', 'a', 'b', 'c')]);
    caret('c');
    run(() => {
      $toggleBlock('number');
    });
    expect(blocks()).toEqual([list('bulleted_list', 'a', 'b'), list('numbered_list', 'c')]);
  });

  it('InsertLine_WhenInAListItem_SplitsItIntoTwoItems', () => {
    const { run, caret, blocks } = setup([list('bulleted_list', 'hello')]);
    caret('hello', 2);
    run($insertLine);
    expect(blocks()).toEqual([list('bulleted_list', 'he', 'llo')]);
  });

  it('InsertLine_WhenOnAnEmptyItem_LeavesTheList', () => {
    const { run, caret, type, blocks, nodes } = setup([list('numbered_list', 'a')]);
    caret('a');
    run($insertLine);
    expect(nodes()).toEqual(['list:a\n\n']);
    run($insertLine);
    expect(nodes()).toEqual(['list:a', 'paragraph:']);
    type('after');
    expect(blocks()).toEqual([list('numbered_list', 'a'), ...paragraphs('after')]);
  });

  it('Backspace_WhenBeforeTheFirstCharacterOfAnItem_UnlistsThatItemInPlace', () => {
    const { run, caret, blocks, caretText } = setup([list('bulleted_list', 'a', 'b', 'c')]);
    caret('b', 0);
    let handled = false;
    run(() => {
      handled = $unformatAtLineStart();
    });
    expect(handled).toBe(true);
    expect(blocks()).toEqual([list('bulleted_list', 'a'), ...paragraphs('b'), list('bulleted_list', 'c')]);
    expect(caretText()).toBe('b');
  });

  it('Backspace_WhenInsideAnItemOrInAParagraph_IsNotHandled', () => {
    const { run, caret } = setup([list('bulleted_list', 'ab'), ...paragraphs('p')]);
    let handled = true;
    caret('ab', 1);
    run(() => {
      handled = $unformatAtLineStart();
    });
    expect(handled).toBe(false);
    handled = true;
    caret('p', 0);
    run(() => {
      handled = $unformatAtLineStart();
    });
    expect(handled).toBe(false);
  });

  it('NestedList_WhenAListSitsInsideAnItem_IsLiftedIntoTheListAroundIt', () => {
    const { run, blocks, nodes } = setup();
    run(() => {
      const outer = $createListNode('bullet');
      const first = $createListItemNode().append($createTextNode('a'));
      const inner = $createListNode('bullet');
      inner.append($createListItemNode().append($createTextNode('b')));
      const wrapper = $createListItemNode().append(inner);
      const last = $createListItemNode().append($createTextNode('c'));
      outer.append(first, wrapper, last);
      $getRoot().clear().append(outer);
    });
    expect(blocks()).toEqual([list('bulleted_list', 'a', 'b', 'c')]);
    expect(nodes()).toEqual(['list:a\n\nb\n\nc']);
  });
});

describe('Shift+Enter', () => {
  it('InsertLine_WhenInAParagraph_StartsANewParagraph', () => {
    const { run, caret, blocks, nodes } = setup(paragraphs('hello'));
    caret('hello', 2);
    run($insertLine);
    expect(nodes()).toEqual(['paragraph:he', 'paragraph:llo']);
    expect(blocks()).toEqual(paragraphs('he', 'llo'));
  });

  it('InsertLine_WhenTextIsSelected_ReplacesItWithTheBreak', () => {
    const { run, select, blocks } = setup(paragraphs('a', 'b', 'c'));
    select('a', 'b');
    run($insertLine);
    expect(blocks()).toEqual(paragraphs('c'));
  });

  it('InsertLine_WhenInAQuote_AddsAQuotedLine', () => {
    const { run, caret, type, blocks, nodes } = setup([{ type: 'quote', children: [text('a')] }]);
    caret('a');
    run($insertLine);
    expect(nodes()).toEqual(['quote:a', 'quote:']);
    type('b');
    expect(blocks()).toEqual([{ type: 'quote', children: [text('a\nb')] }]);
  });

  it('InsertLine_WhenOnAnEmptyQuotedLine_LeavesTheQuote', () => {
    const { run, caret, type, blocks, nodes } = setup([{ type: 'quote', children: [text('a')] }]);
    caret('a');
    run($insertLine);
    run($insertLine);
    expect(nodes()).toEqual(['quote:a', 'paragraph:']);
    type('b');
    expect(blocks()).toEqual([{ type: 'quote', children: [text('a')] }, ...paragraphs('b')]);
  });

  it('InsertLine_WhenInACodeBlock_AddsALineInsideTheBlock', () => {
    const { run, caret, type, blocks, nodes } = setup([{ type: 'code_block', children: [text('a')] }]);
    caret('a');
    run($insertLine);
    type('b');
    expect(nodes()).toEqual(['code:a\nb']);
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a\nb')] }]);
  });

  it('InsertLine_WhenOnTheEmptyLastLineOfACodeBlock_LeavesTheBlock', () => {
    const { run, caret, type, blocks, nodes } = setup([{ type: 'code_block', children: [text('a')] }]);
    caret('a');
    run($insertLine);
    run($insertLine);
    expect(nodes()).toEqual(['code:a', 'paragraph:']);
    type('b');
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a')] }, ...paragraphs('b')]);
  });

  it('InsertLine_WhenInTheMiddleOfACodeLine_SplitsTheLineAndStaysInTheBlock', () => {
    const { run, caret, blocks } = setup([{ type: 'code_block', children: [text('ab')] }]);
    caret('ab', 1);
    run($insertLine);
    run($insertLine);
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a\n\nb')] }]);
  });
});

describe('Leaving a code block', () => {
  it('Fence_WhenThreeBackticksAreTypedInAnEmptyParagraph_StartsACodeBlock', () => {
    const { caretInEmpty, type, blocks, nodes } = setup();
    caretInEmpty();
    type('```');
    expect(nodes()).toEqual(['code:']);
    type('x = 1');
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('x = 1')] }]);
  });

  it('Fence_WhenTypedAfterText_StartsACodeBlockBelowTheText', () => {
    const { caret, type, blocks } = setup(paragraphs('see'));
    caret('see');
    type('```');
    type('code');
    expect(blocks()).toEqual([...paragraphs('see'), { type: 'code_block', children: [text('code')] }]);
  });

  it('Fence_WhenTypedAtTheEndOfACodeBlock_ClosesItAndMovesBelow', () => {
    const { run, caret, type, blocks, nodes } = setup([{ type: 'code_block', children: [text('a')] }]);
    caret('a');
    run($insertLine);
    type('```');
    expect(nodes()).toEqual(['code:a', 'paragraph:']);
    type('done');
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a')] }, ...paragraphs('done')]);
  });

  it('Fence_WhenTypedRightAfterCode_ClosesTheBlockToo', () => {
    const { caret, type, blocks } = setup([{ type: 'code_block', children: [text('a')] }]);
    caret('a');
    type('```');
    type('b');
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a')] }, ...paragraphs('b')]);
  });

  it('ArrowDown_WhenOnTheLastLineOfACodeBlockThatEndsTheMessage_MovesToANewParagraphBelow', () => {
    const { run, caret, type, blocks } = setup([{ type: 'code_block', children: [text('a\nb')] }]);
    let handled = true;
    caret('a');
    run(() => {
      handled = $leaveCodeBlock('down');
    });
    expect(handled).toBe(false);
    caret('b', 0);
    run(() => {
      handled = $leaveCodeBlock('down');
    });
    expect(handled).toBe(true);
    type('c');
    expect(blocks()).toEqual([{ type: 'code_block', children: [text('a\nb')] }, ...paragraphs('c')]);
  });

  it('ArrowUp_WhenOnTheFirstLineOfACodeBlockThatStartsTheMessage_MovesToANewParagraphAbove', () => {
    const { run, caret, type, blocks } = setup([{ type: 'code_block', children: [text('a\nb')] }]);
    let handled = false;
    caret('a');
    run(() => {
      handled = $leaveCodeBlock('up');
    });
    expect(handled).toBe(true);
    type('c');
    expect(blocks()).toEqual([...paragraphs('c'), { type: 'code_block', children: [text('a\nb')] }]);
  });

  it('ArrowDown_WhenABlockFollowsTheCodeBlock_IsLeftToTheBrowser', () => {
    const { run, caret } = setup([{ type: 'code_block', children: [text('a')] }, ...paragraphs('p')]);
    let handled = true;
    caret('a');
    run(() => {
      handled = $leaveCodeBlock('down');
    });
    expect(handled).toBe(false);
  });

  it('Backspace_WhenBeforeTheFirstCharacterOfACodeBlock_TurnsItsLinesIntoParagraphs', () => {
    const { run, caret, blocks } = setup([{ type: 'code_block', children: [text('a\nb')] }]);
    let handled = false;
    caret('b', 0);
    run(() => {
      handled = $unformatAtLineStart();
    });
    expect(handled).toBe(false);
    caret('a', 0);
    run(() => {
      handled = $unformatAtLineStart();
    });
    expect(handled).toBe(true);
    expect(blocks()).toEqual(paragraphs('a', 'b'));
  });

  it('Backspace_WhenBeforeTheFirstCharacterOfAQuotedLine_UnquotesThatLine', () => {
    const { run, caret, blocks } = setup([{ type: 'quote', children: [text('a\nb')] }]);
    caret('b', 0);
    run(() => {
      $unformatAtLineStart();
    });
    expect(blocks()).toEqual([{ type: 'quote', children: [text('a')] }, ...paragraphs('b')]);
  });
});

describe('Placeholder state', () => {
  it('IsPristine_WhenOnlyAnEmptyParagraph_IsTrueAndFalseOnceABlockStarts', () => {
    const { run, caretInEmpty, read } = setup();
    expect(read($isPristine)).toBe(true);
    caretInEmpty();
    run(() => {
      $toggleBlock('quote');
    });
    expect(read($isPristine)).toBe(false);
    run(() => {
      $toggleBlock('quote');
    });
    expect(read($isPristine)).toBe(true);
  });
});

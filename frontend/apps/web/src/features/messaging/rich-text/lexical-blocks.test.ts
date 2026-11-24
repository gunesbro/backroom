import { createEditor, type LexicalEditor } from 'lexical';
import { describe, expect, it } from 'vitest';
import { blocksToText, type Block, type TextNode } from './blocks';
import { $readBlocks, $writeBlocks, EDITOR_NODES } from './lexical-blocks';

function editor(): LexicalEditor {
  return createEditor({
    namespace: 'test',
    nodes: [...EDITOR_NODES],
    onError: (error) => {
      throw error;
    },
  });
}

function roundTrip(blocks: Block[]): Block[] {
  const target = editor();
  target.update(
    () => {
      $writeBlocks(blocks);
    },
    { discrete: true },
  );
  return target.read($readBlocks);
}

function text(value: string, ...types: ('bold' | 'italic' | 'strike' | 'code')[]): TextNode {
  return types.length > 0
    ? { type: 'text', text: value, marks: types.map((type) => ({ type })) }
    : { type: 'text', text: value };
}

describe('Lexical block serialization', () => {
  it('RoundTrip_WhenAllBlockTypes_KeepsThem', () => {
    const blocks: Block[] = [
      {
        type: 'paragraph',
        children: [
          text('plain '),
          text('bold', 'bold'),
          text(' '),
          text('both', 'bold', 'italic'),
          text(' '),
          text('gone', 'strike'),
          text(' '),
          text('x', 'code'),
        ],
      },
      { type: 'quote', children: [text('quoted')] },
      {
        type: 'bulleted_list',
        children: [
          { type: 'list_item', children: [text('a')] },
          { type: 'list_item', children: [text('b', 'italic')] },
        ],
      },
      { type: 'numbered_list', children: [{ type: 'list_item', children: [text('one')] }] },
      { type: 'code_block', children: [text('const a = 1;\nconst b = 2;')] },
      {
        type: 'paragraph',
        children: [
          {
            type: 'text',
            text: 'site',
            marks: [{ type: 'bold' }, { type: 'link', href: 'https://example.com/' }],
          },
        ],
      },
    ];
    expect(roundTrip(blocks)).toEqual(blocks);
  });

  it('RoundTrip_WhenLineBreakInParagraph_KeepsNewlineInText', () => {
    const blocks: Block[] = [{ type: 'paragraph', children: [text('line one\nline two')] }];
    expect(roundTrip(blocks)).toEqual(blocks);
  });

  it('RoundTrip_WhenUnsafeLink_DropsTheLinkAndKeepsTheText', () => {
    const result = roundTrip([
      {
        type: 'paragraph',
        children: [{ type: 'text', text: 'evil', marks: [{ type: 'link', href: 'javascript:alert(1)' }] }],
      },
    ]);
    expect(result).toEqual([{ type: 'paragraph', children: [{ type: 'text', text: 'evil' }] }]);
  });

  it('RoundTrip_WhenEmpty_HasNoBlocks', () => {
    expect(roundTrip([])).toEqual([]);
  });

  it('Read_WhenControlCharactersAndLoneSurrogates_RemovesThem', () => {
    const result = roundTrip([{ type: 'paragraph', children: [text('a\u0007b\ud800c')] }]);
    expect(blocksToText(result)).toBe('abc');
  });

  it('Read_WhenTextIsNotNormalized_ProducesNfc', () => {
    const result = roundTrip([{ type: 'paragraph', children: [text('é')] }]);
    expect(blocksToText(result)).toBe('é');
  });
});

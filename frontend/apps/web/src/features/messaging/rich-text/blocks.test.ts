import { describe, expect, it } from 'vitest';
import {
  MAX_BLOCKS_JSON_BYTES,
  blocksJsonBytes,
  blocksToText,
  canonicalMarks,
  derivedMentionNames,
  hrefFromInput,
  mergeRuns,
  parseBlocks,
  safeHref,
  type Block,
} from './blocks';

describe('safeHref', () => {
  it.each([
    ['https://example.com/a?b=1', 'https://example.com/a?b=1'],
    ['http://example.com', 'http://example.com/'],
    ['mailto:alice@example.test', 'mailto:alice@example.test'],
    ['  https://example.com  ', 'https://example.com/'],
  ])('SafeHref_WhenAllowedScheme_ReturnsAddress (%s)', (input, expected) => {
    expect(safeHref(input)).toBe(expected);
  });

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    'java\tscript:alert(1)',
    ' javascript:alert(1)',
    'data:text/html,hello',
    'vbscript:msgbox(1)',
    'file:///etc/passwd',
    'tel:+900000000',
    '/relative/path',
    '//example.com',
    '',
    'not a url',
  ])('SafeHref_WhenSchemeIsNotAllowed_ReturnsNull (%s)', (input) => {
    expect(safeHref(input)).toBeNull();
  });

  it('SafeHref_WhenLongerThan2048_ReturnsNull', () => {
    expect(safeHref(`https://example.com/${'a'.repeat(2040)}`)).toBeNull();
    expect(safeHref(`https://example.com/${'a'.repeat(2000)}`)).not.toBeNull();
  });
});

describe('hrefFromInput', () => {
  it('HrefFromInput_WhenNoScheme_AssumesHttps', () => {
    expect(hrefFromInput('example.com/path')).toBe('https://example.com/path');
    expect(hrefFromInput('example.com:8080')).toBe('https://example.com:8080/');
  });

  it.each(['javascript:alert(1)', 'data:text/html,x', 'ftp://example.com', '//evil.test', '   '])(
    'HrefFromInput_WhenOtherScheme_ReturnsNull (%s)',
    (input) => {
      expect(hrefFromInput(input)).toBeNull();
    },
  );
});

describe('canonicalMarks', () => {
  it('CanonicalMarks_WhenUnsorted_OrdersAndDropsUnsafeLink', () => {
    expect(
      canonicalMarks([{ type: 'link', href: 'javascript:alert(1)' }, { type: 'code' }, { type: 'bold' }]),
    ).toEqual([{ type: 'bold' }, { type: 'code' }]);
  });
});

describe('mergeRuns', () => {
  it('MergeRuns_WhenSameMarks_JoinsAndDropsEmpty', () => {
    expect(
      mergeRuns([
        { type: 'text', text: 'a', marks: [{ type: 'bold' }] },
        { type: 'text', text: '' },
        { type: 'text', text: 'b', marks: [{ type: 'bold' }] },
        { type: 'text', text: 'c' },
      ]),
    ).toEqual([
      { type: 'text', text: 'ab', marks: [{ type: 'bold' }] },
      { type: 'text', text: 'c' },
    ]);
  });
});

describe('blocksToText', () => {
  it('BlocksToText_WhenMixedBlocks_PutsEachBlockAndItemOnItsOwnLine', () => {
    const blocks: Block[] = [
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'Hello ' },
          { type: 'text', text: 'world', marks: [{ type: 'bold' }] },
        ],
      },
      {
        type: 'bulleted_list',
        children: [
          { type: 'list_item', children: [{ type: 'text', text: 'one' }] },
          { type: 'list_item', children: [{ type: 'text', text: 'two' }] },
        ],
      },
      { type: 'code_block', children: [{ type: 'text', text: 'a\nb' }] },
    ];
    expect(blocksToText(blocks)).toBe('Hello world\none\ntwo\na\nb');
  });

  it('BlocksToText_WhenOnlyWhitespace_IsEmpty', () => {
    expect(blocksToText([{ type: 'paragraph', children: [{ type: 'text', text: ' \n ' }] }])).toBe('');
  });

  it('BlocksToText_WhenTurkishText_KeepsDottedCapitalI', () => {
    expect(blocksToText([{ type: 'paragraph', children: [{ type: 'text', text: 'İstanbul ığ' }] }])).toBe(
      'İstanbul ığ',
    );
  });
});

describe('blocksJsonBytes', () => {
  it('BlocksJsonBytes_CountsUtf8Bytes', () => {
    const blocks: Block[] = [{ type: 'paragraph', children: [{ type: 'text', text: 'ğ' }] }];
    expect(blocksJsonBytes(blocks)).toBe(JSON.stringify(blocks).length + 1);
    expect(MAX_BLOCKS_JSON_BYTES).toBe(32768);
  });
});

describe('parseBlocks', () => {
  it('ParseBlocks_WhenUnknownBlockTypes_SkipsThem', () => {
    const parsed = parseBlocks([
      { type: 'table', children: [] },
      {
        type: 'paragraph',
        children: [{ type: 'text', text: 'kept' }, { type: 'mention', accountId: 'x' }, { type: 'sparkle' }],
      },
      { type: 'heading', children: [{ type: 'text', text: 'dropped' }] },
    ]);
    expect(parsed).toEqual([
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'kept' },
          { type: 'mention', accountId: 'x' },
        ],
      },
    ]);
  });

  it('ParseBlocks_WhenNothingKnown_ReturnsNull', () => {
    expect(parseBlocks([{ type: 'table' }])).toBeNull();
    expect(parseBlocks('plain string')).toBeNull();
    expect(parseBlocks(undefined)).toBeNull();
    expect(parseBlocks([])).toBeNull();
  });

  it('ParseBlocks_WhenUnsafeLinkOrUnknownMark_DropsThatMark', () => {
    const parsed = parseBlocks([
      {
        type: 'paragraph',
        children: [
          {
            type: 'text',
            text: 'x',
            marks: [{ type: 'link', href: 'javascript:alert(1)' }, { type: 'glow' }, { type: 'italic' }],
          },
        ],
      },
    ]);
    expect(parsed).toEqual([
      { type: 'paragraph', children: [{ type: 'text', text: 'x', marks: [{ type: 'italic' }] }] },
    ]);
  });
});

describe('derivedMentionNames', () => {
  it('DerivedMentionNames_WhenNamesHaveSpacesAndTurkishLetters_FindsEachBetweenItsNeighbours', () => {
    const blocks = parseBlocks([
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'Merhaba ' },
          { type: 'mention', accountId: 'a' },
          { type: 'text', text: ' ve ' },
          { type: 'mention', accountId: 'b' },
        ],
      },
      {
        type: 'quote',
        children: [
          { type: 'mention', special: 'here' },
          { type: 'text', text: ' tamam' },
        ],
      },
    ]);

    const names = derivedMentionNames(blocks ?? [], 'Merhaba @\u0130pek \u015Eahin ve @Ali\n@burada tamam');

    expect([...names.values()]).toEqual(['\u0130pek \u015Eahin', 'Ali', 'burada']);
  });

  it('DerivedMentionNames_WhenTextDoesNotMatch_ReturnsWhatItFoundWithoutThrowing', () => {
    const blocks = parseBlocks([
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'hi ' },
          { type: 'mention', accountId: 'a' },
        ],
      },
    ]);

    expect(derivedMentionNames(blocks ?? [], 'something else').size).toBe(0);
  });
});

describe('derivedMentionNames boundaries', () => {
  it('DerivedMentionNames_WhenNextRunIsNotFoundOrMentionsAreAdjacent_GivesNoNameInsteadOfGuessing', () => {
    const adjacent = parseBlocks([
      {
        type: 'paragraph',
        children: [
          { type: 'mention', accountId: 'a' },
          { type: 'mention', accountId: 'b' },
        ],
      },
    ]);
    const missing = parseBlocks([
      {
        type: 'paragraph',
        children: [
          { type: 'mention', accountId: 'a' },
          { type: 'text', text: ' says hello' },
        ],
      },
    ]);

    expect(derivedMentionNames(adjacent ?? [], '@Ann Bob').size).toBe(0);
    expect(derivedMentionNames(missing ?? [], '@Ann says hi').size).toBe(0);
  });
});

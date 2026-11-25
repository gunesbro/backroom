import { $createCodeNode } from '@lexical/code';
import {
  $createParagraphNode,
  $createTextNode,
  $getRoot,
  $getSelection,
  $isRangeSelection,
  TextNode,
  createEditor,
  type LexicalEditor,
  type LexicalNode,
} from 'lexical';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { blocksToText } from './blocks';
import { $emojiFromText, $isEmojiNode, $textFromEmoji, EmojiNode } from './emoji-node';
import { $readBlocks, EDITOR_NODES } from './lexical-blocks';

const FAMILY = '👨\u{200D}👩\u{200D}👧';

/** An editor with the emoji transforms of the composer, on an element of the page. */
function editor(): { editor: LexicalEditor; root: HTMLElement } {
  const target = createEditor({
    namespace: 'test',
    nodes: [...EDITOR_NODES],
    onError: (error) => {
      throw error;
    },
  });
  const root = document.createElement('div');
  root.contentEditable = 'true';
  document.body.append(root);
  target.setRootElement(root);
  target.registerNodeTransform(TextNode, $emojiFromText);
  target.registerNodeTransform(EmojiNode, $textFromEmoji);
  return { editor: target, root };
}

function update(target: LexicalEditor, change: () => void): void {
  target.update(change, { discrete: true });
}

/** Types into an empty paragraph, like a person or a paste does. */
function write(target: LexicalEditor, ...texts: string[]): void {
  update(target, () => {
    const paragraph = $createParagraphNode();
    $getRoot().clear().append(paragraph);
    paragraph.select();
  });
  for (const text of texts) {
    update(target, () => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) selection.insertRawText(text);
    });
  }
}

type Modify = (alter: string, direction: string, granularity: string) => void;

/**
 * jsdom has no `Selection.modify`, which the editor uses to find what a Backspace or Delete removes. This stand-in
 * moves the caret by one UTF-16 unit, into the next text when it is at an edge: less than any browser moves (a
 * whole emoji), so it lands inside an emoji, which then has to go as a whole all the same.
 */
const moveOneUnit: Modify = function (this: Selection, _alter, direction) {
  const { startContainer: container, startOffset: offset } = this.getRangeAt(0);
  const texts = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
  texts.currentNode = container;
  if (direction === 'backward') {
    if (offset > 0) this.collapse(container, offset - 1);
    else if (texts.previousNode()) this.collapse(texts.currentNode, (texts.currentNode as Text).length - 1);
  } else if (offset < (container as Text).length) this.collapse(container, offset + 1);
  else if (texts.nextNode()) this.collapse(texts.currentNode, 1);
};

beforeEach(() => {
  (Selection.prototype as unknown as { modify: Modify }).modify = moveOneUnit;
});
afterEach(() => {
  delete (Selection.prototype as unknown as { modify?: Modify }).modify;
  document.body.replaceChildren();
});

const describeNode = (node: LexicalNode) =>
  `${$isEmojiNode(node) ? 'emoji' : node.getType()}:${node.getTextContent()}`;

/** The text nodes of the editor in order, as `type:text`. */
function leaves(target: LexicalEditor): string[] {
  return target.read(() => $getRoot().getAllTextNodes().map(describeNode));
}

describe('Emoji in the editor', () => {
  it('TypedOrPastedEmoji_BecomeEmojiNodes_AndTheTextAroundThemStaysText', () => {
    const { editor: target } = editor();

    write(target, `Hi 😀👍🏽 and ${FAMILY} or 🇹🇷 1\u{FE0F}\u{20E3}!`);

    expect(leaves(target)).toEqual([
      'text:Hi ',
      'emoji:😀',
      'emoji:👍🏽',
      'text: and ',
      `emoji:${FAMILY}`,
      'text: or ',
      'emoji:🇹🇷',
      'text: ',
      'emoji:1\u{FE0F}\u{20E3}',
      'text:!',
    ]);
  });

  it('Blocks_ReadFromTheEditor_ArePlainUnicodeText', () => {
    const { editor: target } = editor();
    const text = `Well done 🎉 ${FAMILY}`;

    write(target, text);

    const blocks = target.read($readBlocks);
    // One run: an emoji is text to the server, and nothing in the blocks tells that it was drawn as an image.
    expect(blocks).toEqual([{ type: 'paragraph', children: [{ type: 'text', text }] }]);
    expect(blocksToText(blocks)).toBe(text);
    expect(target.read(() => $getRoot().getTextContent())).toBe(text);
  });

  it('EditorState_AsJson_KeepsTheEmojiAsTextAndComesBackAsEmojiNodes', () => {
    const { editor: target } = editor();
    write(target, 'a 😀');

    const json = JSON.stringify(target.getEditorState().toJSON());

    expect(json).toContain('"type":"emoji"');
    expect(json).toContain('"text":"😀"');
    expect(json).not.toContain('assets');
    const { editor: other } = editor();
    other.setEditorState(other.parseEditorState(json));
    expect(leaves(other)).toEqual(['text:a ', 'emoji:😀']);
  });

  it('Backspace_AfterAnEmoji_RemovesTheWholeEmoji', () => {
    const { editor: target } = editor();
    write(target, `ab${FAMILY}`);

    update(target, () => {
      const selection = $getSelection();
      if ($isRangeSelection(selection)) selection.deleteCharacter(true);
    });

    expect(leaves(target)).toEqual(['text:ab']);
  });

  it('Delete_BeforeAnEmoji_RemovesTheWholeEmoji', () => {
    const { editor: target } = editor();
    write(target, `ab👍🏽cd`);

    update(target, () => {
      $getRoot().getAllTextNodes()[0]?.select(2, 2);
      const selection = $getSelection();
      if ($isRangeSelection(selection)) selection.deleteCharacter(false);
    });

    expect(target.read(() => $getRoot().getTextContent())).toBe('abcd');
  });

  it('Typing_AfterAnEmoji_GoesOnAfterIt_NotIntoIt', () => {
    const { editor: target } = editor();

    write(target, 'a😀', 'b', '🎉', 'c');

    expect(leaves(target)).toEqual(['text:a', 'emoji:😀', 'text:b', 'emoji:🎉', 'text:c']);
  });

  it('Emoji_InCode_StayCharacters', () => {
    const { editor: target } = editor();
    update(target, () => {
      const paragraph = $createParagraphNode();
      paragraph.append($createTextNode('x 😀').toggleFormat('code'));
      const code = $createCodeNode();
      code.append($createTextNode('print("🎉")'));
      $getRoot().clear().append(paragraph, code);
    });

    expect(leaves(target)).toEqual(['text:x 😀', 'text:print("🎉")']);
  });

  it('Emoji_ThatBecomesCode_TurnsBackIntoText', () => {
    const { editor: target } = editor();
    write(target, '😀');

    update(target, () => {
      $getRoot().getAllTextNodes()[0]?.toggleFormat('code');
    });

    expect(leaves(target)).toEqual(['text:😀']);
  });

  it('Element_ShowsTheImageOfTheAppsOwnSet_OverTheCharacter', () => {
    const { editor: target, root } = editor();

    write(target, 'a 👍🏽');

    const element = root.querySelector<HTMLElement>('.bm-rt-emoji');
    expect(element?.textContent).toBe('👍🏽');
    expect(element?.style.backgroundImage).toBe('url("/assets/emoji-test/1f44d-1f3fd.svg")');
    expect(root.querySelectorAll('img')).toHaveLength(0);
    expect(root.textContent).toBe('a 👍🏽');
  });

  it('Emoji_CopiedOutOfTheEditorAsHtml_IsThePlainCharacter', () => {
    const { editor: target } = editor();
    write(target, '😀');

    const html = target.read(() => {
      const node = $getRoot().getAllTextNodes()[0];
      const { element } = node?.exportDOM(target) ?? {};
      return element instanceof HTMLElement ? element.outerHTML : '';
    });

    expect(html).toContain('😀');
    expect(html).not.toContain('bm-rt-emoji');
    expect(html).not.toContain('assets');
  });
});

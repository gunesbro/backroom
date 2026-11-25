// Emoji in the editor: drawn as the same images as in messages, and still text to everything else.
//
// An emoji is a text node that holds the emoji's characters, so the blocks read from the editor, the editor's own
// copy and cut, undo and the character count see plain Unicode text. Only its element differs: the characters are
// invisible and the image is painted behind them. The node is a token: the caret cannot go inside it and deleting
// removes it whole.
import { $isCodeNode } from '@lexical/code';
import {
  $applyNodeReplacement,
  $createTextNode,
  TextNode,
  type DOMExportOutput,
  type EditorConfig,
  type LexicalEditor,
  type NodeKey,
  type SerializedTextNode,
} from 'lexical';
import { emojiImageUrl, firstEmoji } from '@/features/emoji';

const CLASS = 'bm-rt-emoji';

function paint(dom: HTMLElement, emoji: string): void {
  dom.classList.add(CLASS);
  dom.style.backgroundImage = `url("${emojiImageUrl(emoji)}")`;
}

export class EmojiNode extends TextNode {
  static override getType(): string {
    return 'emoji';
  }

  static override clone(node: EmojiNode): EmojiNode {
    return new EmojiNode(node.__text, node.__key);
  }

  static override importJSON(serialized: SerializedTextNode): EmojiNode {
    return $createEmojiNode(serialized.text).updateFromJSON(serialized);
  }

  constructor(text = '', key?: NodeKey) {
    super(text, key);
  }

  override createDOM(config: EditorConfig, editor?: LexicalEditor): HTMLElement {
    const dom = super.createDOM(config, editor);
    paint(dom, this.__text);
    return dom;
  }

  override updateDOM(prevNode: this, dom: HTMLElement, config: EditorConfig): boolean {
    const replace = super.updateDOM(prevNode, dom, config);
    if (!replace && prevNode.__text !== this.__text) paint(dom, this.__text);
    return replace;
  }

  /** What leaves the editor as HTML (a copy) is the plain character, without our class or the image's address. */
  override exportDOM(editor: LexicalEditor): DOMExportOutput {
    const output = super.exportDOM(editor);
    if (output.element instanceof HTMLElement) {
      for (const element of [output.element, ...output.element.querySelectorAll<HTMLElement>(`.${CLASS}`)]) {
        element.classList.remove(CLASS);
        element.style.backgroundImage = '';
      }
    }
    return output;
  }

  override canInsertTextBefore(): boolean {
    return false;
  }

  override canInsertTextAfter(): boolean {
    return false;
  }
}

export function $createEmojiNode(emoji: string): EmojiNode {
  return $applyNodeReplacement(new EmojiNode(emoji).setMode('token'));
}

export function $isEmojiNode(node: unknown): node is EmojiNode {
  return node instanceof EmojiNode;
}

/** Text that stays text: inline code and code blocks show characters as they are. */
function isCode(node: TextNode): boolean {
  return node.hasFormat('code') || $isCodeNode(node.getParent());
}

/**
 * Turns the first emoji of a text node into an emoji node; the editor runs it again on what is left, so typed,
 * pasted and picked emoji all become emoji nodes. Register it as a transform of `TextNode`.
 */
export function $emojiFromText(node: TextNode): void {
  if (!node.isSimpleText() || node.isComposing() || isCode(node)) return;
  const match = firstEmoji(node.getTextContent());
  if (!match) return;
  const pieces = match.start === 0 ? node.splitText(match.end) : node.splitText(match.start, match.end);
  const target = match.start === 0 ? pieces[0] : pieces[1];
  if (!target) return;
  target.replace($createEmojiNode(match.emoji).setFormat(target.getFormat()));
}

/**
 * Turns an emoji node back into text where emoji are not drawn (it became code) or when it no longer holds exactly
 * one emoji. Register it as a transform of `EmojiNode`.
 */
export function $textFromEmoji(node: EmojiNode): void {
  const text = node.getTextContent();
  const match = firstEmoji(text);
  if (!isCode(node) && match?.start === 0 && match.end === text.length) return;
  node.replace($createTextNode(text).setFormat(node.getFormat()));
}

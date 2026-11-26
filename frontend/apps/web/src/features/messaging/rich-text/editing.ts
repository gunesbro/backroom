// How the composer edits blocks, line by line. A line is a top-level paragraph, one quoted line (every quoted line is
// its own quote node; neighbours are sent as one quote), a list item, or a line of a code block. Enter sends, so
// Shift+Enter is the only key that makes a line; the toolbar changes exactly the lines the selection covers.
import { $createCodeNode, $isCodeNode } from '@lexical/code';
import {
  $createListItemNode,
  $createListNode,
  $handleListInsertParagraph,
  $isListItemNode,
  $isListNode,
  type ListNode,
} from '@lexical/list';
import { $createQuoteNode, $isQuoteNode } from '@lexical/rich-text';
import {
  $createLineBreakNode,
  $createParagraphNode,
  $createRangeSelection,
  $createTextNode,
  $getEditor,
  $getNodeByKey,
  $getRoot,
  $getSelection,
  $isElementNode,
  $isLineBreakNode,
  $isParagraphNode,
  $isRangeSelection,
  $isRootNode,
  $setSelection,
  type ElementNode,
  type LexicalNode,
  type PointType,
  type RangeSelection,
  type TextNode,
} from 'lexical';

export type BlockKind = 'paragraph' | 'bullet' | 'number' | 'quote' | 'code';

const FENCE = '```';

/** What kind of block a top-level node is. */
export function blockKindOf(top: LexicalNode | null): BlockKind {
  if ($isListNode(top)) return top.getListType() === 'number' ? 'number' : 'bullet';
  if ($isQuoteNode(top)) return 'quote';
  if ($isCodeNode(top)) return 'code';
  return 'paragraph';
}

/** True while there is nothing but one empty paragraph, which is when the placeholder shows. */
export function $isPristine(): boolean {
  const root = $getRoot();
  const only = root.getFirstChild();
  return root.getChildrenSize() === 1 && $isParagraphNode(only) && only.getChildrenSize() === 0;
}

/** The lines of a code block: its children between the line breaks. */
function codeLines(code: ElementNode): LexicalNode[][] {
  const lines: LexicalNode[][] = [[]];
  for (const child of code.getChildren()) {
    if ($isLineBreakNode(child)) lines.push([]);
    else (lines[lines.length - 1] as LexicalNode[]).push(child);
  }
  return lines;
}

function lineCount(top: ElementNode): number {
  if ($isListNode(top)) return Math.max(top.getChildrenSize(), 1);
  if ($isCodeNode(top)) return top.getChildren().filter($isLineBreakNode).length + 1;
  return 1;
}

type Position = { top: ElementNode; index: number };

/** The line a selection point is on: a top-level node and the line (list item, code line) inside it. */
function $position(point: PointType, edge: 'start' | 'end'): Position | null {
  const node = point.getNode();
  if ($isRootNode(node)) {
    // A point on the root itself (select all): the block before or after it.
    const child = node.getChildAtIndex(
      edge === 'start' ? Math.min(point.offset, node.getChildrenSize() - 1) : Math.max(point.offset - 1, 0),
    );
    if (!$isElementNode(child)) return null;
    return { top: child, index: edge === 'start' ? 0 : lineCount(child) - 1 };
  }
  const top = node.getTopLevelElement();
  if (top === null) return null;
  if ($isListNode(top)) {
    if (node.is(top)) {
      const index = edge === 'start' ? point.offset : point.offset - 1;
      return { top, index: Math.min(Math.max(index, 0), lineCount(top) - 1) };
    }
    let item: LexicalNode = node;
    for (let parent = item.getParent(); parent !== null && !parent.is(top); parent = item.getParent()) {
      item = parent;
    }
    return { top, index: item.getIndexWithinParent() };
  }
  if ($isCodeNode(top)) {
    const before = node.is(top) ? top.getChildren().slice(0, point.offset) : node.getPreviousSiblings();
    return { top, index: before.filter($isLineBreakNode).length };
  }
  return { top, index: 0 };
}

/** True when the point is before the first character of its line. */
function $atLineStart(point: PointType): boolean {
  let node: LexicalNode = point.getNode();
  if (point.type === 'element' && $isCodeNode(node)) {
    return point.offset === 0 || $isLineBreakNode(node.getChildAtIndex(point.offset - 1));
  }
  if (point.offset !== 0) return false;
  for (;;) {
    const parent = node.getParent();
    if (parent === null || $isRootNode(parent) || $isListItemNode(node)) return true;
    const previous = node.getPreviousSibling();
    if ($isCodeNode(parent)) return previous === null || $isLineBreakNode(previous);
    if (previous !== null) return false;
    node = parent;
  }
}

type Range = { tops: ElementNode[]; from: number; to: number };

/** The lines a selection covers: the top-level nodes it touches, the first line in the first and the last in the last. */
function $selectedRange(selection: RangeSelection): Range | null {
  const backward = selection.isBackward();
  const startPoint = backward ? selection.focus : selection.anchor;
  const endPoint = backward ? selection.anchor : selection.focus;
  const start = $position(startPoint, 'start');
  let end = $position(endPoint, 'end');
  if (!start || !end) return null;
  // A selection that ends before the first character of a line (Shift+Down, a triple click) does not cover that line.
  if (
    !selection.isCollapsed() &&
    !$isRootNode(endPoint.getNode()) &&
    $atLineStart(endPoint) &&
    !(end.top.is(start.top) && end.index === start.index)
  ) {
    if (end.index > 0) {
      end = { top: end.top, index: end.index - 1 };
    } else {
      const previous = end.top.getPreviousSibling();
      if ($isElementNode(previous)) end = { top: previous, index: lineCount(previous) - 1 };
    }
  }
  const tops: ElementNode[] = [];
  for (let top: LexicalNode | null = start.top; top !== null; top = top.getNextSibling()) {
    if ($isElementNode(top)) tops.push(top);
    if (top.is(end.top)) return { tops, from: start.index, to: end.index };
  }
  return null;
}

function fillCode(code: ElementNode, lines: readonly (readonly LexicalNode[])[]): void {
  for (const line of lines) {
    if (code.getChildrenSize() > 0) code.append($createLineBreakNode());
    code.append(...line);
  }
}

/**
 * Turns the lines of a range into blocks of one kind, in place: a list or a code block the range covers only partly
 * is split around it. Returns the new line nodes (for a code block, the block).
 */
function $convert({ tops, from, to }: Range, target: BlockKind): ElementNode[] {
  const first = tops[0] as ElementNode;
  const lines: LexicalNode[][] = [];
  const emptied: ElementNode[] = [];
  for (const [position, top] of tops.entries()) {
    const start = position === 0 ? from : 0;
    const end = position === tops.length - 1 ? to : Number.POSITIVE_INFINITY;
    if ($isListNode(top)) {
      const items = top.getChildren();
      const rest = items.slice(end + 1);
      if (rest.length > 0) {
        const tail = $createListNode(top.getListType());
        top.insertAfter(tail);
        tail.append(...rest);
      }
      for (const item of items.slice(start, end + 1)) {
        lines.push($isElementNode(item) ? item.getChildren() : []);
        if ($isElementNode(item)) emptied.push(item);
      }
      if (start === 0) emptied.push(top);
    } else if ($isCodeNode(top)) {
      const all = codeLines(top);
      const rest = all.slice(end + 1);
      if (rest.length > 0) {
        const tail = $createCodeNode();
        top.insertAfter(tail);
        fillCode(tail, rest);
      }
      lines.push(...all.slice(start, end + 1));
      if (start === 0) emptied.push(top);
    } else {
      lines.push(top.getChildren());
      emptied.push(top);
    }
  }

  const created: ElementNode[] = [];
  let result: ElementNode[];
  if (target === 'code') {
    // Code has no marks and no links: only the characters move.
    const code = $createCodeNode();
    for (const [index, line] of lines.entries()) {
      if (index > 0) code.append($createLineBreakNode());
      const text = line.map((node) => node.getTextContent()).join('');
      for (const [part, piece] of text.split('\n').entries()) {
        if (part > 0) code.append($createLineBreakNode());
        if (piece.length > 0) code.append($createTextNode(piece));
      }
      for (const node of line) {
        if ($isCodeNode(node.getParent())) node.remove();
      }
    }
    created.push(code);
    result = [code];
  } else if (target === 'bullet' || target === 'number') {
    const list = $createListNode(target);
    result = lines.map((line) => {
      const item = $createListItemNode();
      item.append(...line);
      list.append(item);
      return item;
    });
    created.push(list);
  } else {
    result = lines.map((line) => {
      const block = target === 'quote' ? $createQuoteNode() : $createParagraphNode();
      block.append(...line);
      return block;
    });
    created.push(...result);
  }

  let after: LexicalNode = first;
  for (const node of created) {
    after.insertAfter(node);
    after = node;
  }
  // What stays of a code block that was covered from a later line on: its lines before, without the breaks after.
  if ($isCodeNode(first) && from > 0) {
    let breaks = 0;
    for (const child of first.getChildren()) {
      if (!$isLineBreakNode(child)) continue;
      breaks += 1;
      if (breaks >= from) child.remove();
    }
  }
  for (const node of emptied) {
    if (node.isAttached()) node.remove();
  }

  // Neighbours of the same kind become one block: a list keeps counting, a code block stays one box.
  const block = created[0] as ElementNode;
  if ($isListNode(block) || $isCodeNode(block)) {
    const previous = block.getPreviousSibling();
    const next = block.getNextSibling();
    for (const [neighbour, before] of [
      [previous, true],
      [next, false],
    ] as const) {
      if (!$isElementNode(neighbour) || blockKindOf(neighbour) !== blockKindOf(block)) continue;
      const moved = neighbour.getChildren();
      if ($isCodeNode(block)) {
        const separator = $createLineBreakNode();
        if (before) block.splice(0, 0, [...moved, separator]);
        else block.append(separator, ...moved);
      } else if (before) {
        block.splice(0, 0, moved);
      } else {
        block.append(...moved);
      }
      neighbour.remove();
    }
  }
  return result;
}

type SavedPoint = { key: string; offset: number; type: 'text' | 'element' };

function savePoint(point: PointType): SavedPoint {
  return { key: point.key, offset: point.offset, type: point.type };
}

/** Converts a range and puts the selection back: on the same characters where they still exist, else on the new lines. */
function $convertKeepingSelection(selection: RangeSelection, range: Range, target: BlockKind): void {
  const collapsed = selection.isCollapsed();
  const anchor = savePoint(selection.anchor);
  const focus = savePoint(selection.focus);
  const created = $convert(range, target);
  const alive = (point: SavedPoint) =>
    point.type === 'text' && ($getNodeByKey(point.key)?.isAttached() ?? false);
  if (alive(anchor) && alive(focus)) {
    const restored = $createRangeSelection();
    restored.anchor.set(anchor.key, anchor.offset, 'text');
    restored.focus.set(focus.key, focus.offset, 'text');
    $setSelection(restored);
    return;
  }
  const last = created[created.length - 1];
  const start = created[0];
  if (!last || !start) return;
  if (collapsed) {
    last.selectEnd();
    return;
  }
  const from = savePoint(start.selectStart().anchor);
  last.selectEnd().anchor.set(from.key, from.offset, from.type);
}

/**
 * The toolbar's list, quote and code block buttons: the lines the selection covers (the caret's line without one)
 * become that block, or plain paragraphs again when all of them already are.
 */
export function $toggleBlock(kind: Exclude<BlockKind, 'paragraph'>): void {
  const selection = $getSelection();
  if (!$isRangeSelection(selection)) return;
  const range = $selectedRange(selection);
  if (!range) return;
  const on = range.tops.every((top) => blockKindOf(top) === kind);
  $convertKeepingSelection(selection, range, on ? 'paragraph' : kind);
}

/** Cuts a quoted line in two at the caret; the part after it is a quoted line too. */
function $splitQuotedLine(selection: RangeSelection): void {
  const next = selection.insertParagraph();
  if (next !== null && !$isQuoteNode(next)) {
    const quote = $createQuoteNode();
    next.replace(quote, true);
    quote.selectStart();
  }
}

/**
 * Plain text with line breaks, pasted into a quote or a code block: every line stays in the block, as if it had been
 * typed with Shift+Enter between the lines (the editor's own paste would put the lines after the first into new
 * paragraphs). False when the caret is somewhere else or there is nothing to do, and the editor pastes as usual.
 */
export function $pasteLines(text: string): boolean {
  let selection = $getSelection();
  if (!$isRangeSelection(selection)) return false;
  const top = selection.anchor.getNode().getTopLevelElement();
  const lines = text.replace(/\r\n?/g, '\n').replace(/\n$/, '').split('\n');
  // One line into a quote keeps the editor's paste, which also brings bold, italic and links along.
  if (!$isCodeNode(top) && !($isQuoteNode(top) && lines.length > 1)) return false;
  if (!selection.isCollapsed()) selection.removeText();
  for (const [index, line] of lines.entries()) {
    selection = $getSelection();
    if (!$isRangeSelection(selection)) return true;
    if (index > 0) {
      if ($isCodeNode(top)) selection.insertLineBreak();
      else $splitQuotedLine(selection);
      selection = $getSelection();
      if (!$isRangeSelection(selection)) return true;
    }
    if (line.length > 0) selection.insertText(line);
  }
  return true;
}

/**
 * Shift+Enter. A new paragraph, a new quoted line, a new list item or a new line inside a code block; on an empty
 * list item, an empty quoted line or the empty last line of a code block it leaves the block instead.
 */
export function $insertLine(): void {
  let selection = $getSelection();
  if (!$isRangeSelection(selection)) return;
  if (!selection.isCollapsed()) {
    selection.removeText();
    selection = $getSelection();
    if (!$isRangeSelection(selection)) return;
  }
  const { anchor } = selection;
  const top = anchor.getNode().getTopLevelElement();
  if ($isCodeNode(top)) {
    const last = top.getLastChild();
    const atEnd =
      anchor.type === 'element' && anchor.getNode().is(top) && anchor.offset === top.getChildrenSize();
    if (atEnd && (last === null || $isLineBreakNode(last))) {
      const paragraph = $createParagraphNode();
      if (last === null) {
        top.replace(paragraph);
      } else {
        last.remove();
        top.insertAfter(paragraph);
      }
      paragraph.select();
      return;
    }
    selection.insertLineBreak();
    return;
  }
  if ($isQuoteNode(top)) {
    // Only a line with no characters at all is empty: spaces are text a person typed, and they stay quoted.
    if (top.getTextContentSize() === 0) {
      const paragraph = $createParagraphNode();
      top.replace(paragraph);
      paragraph.select();
      return;
    }
    $splitQuotedLine(selection);
    return;
  }
  if ($isListNode(top) && $handleListInsertParagraph()) return;
  selection.insertParagraph();
}

/**
 * Backspace before the first character of a list item or a quoted line takes that line out of its block, and before
 * the first character of a code block turns the block back into paragraphs. False when the key means something else.
 */
export function $unformatAtLineStart(): boolean {
  const selection = $getSelection();
  if (!$isRangeSelection(selection) || !selection.isCollapsed()) return false;
  if (!$atLineStart(selection.anchor)) return false;
  const position = $position(selection.anchor, 'start');
  if (!position) return false;
  const kind = blockKindOf(position.top);
  if (kind === 'paragraph') return false;
  if (kind === 'code') {
    // Inside the block Backspace joins two lines, as everywhere.
    if (position.index > 0) return false;
    $convertKeepingSelection(
      selection,
      { tops: [position.top], from: 0, to: lineCount(position.top) - 1 },
      'paragraph',
    );
    return true;
  }
  $convertKeepingSelection(
    selection,
    { tops: [position.top], from: position.index, to: position.index },
    'paragraph',
  );
  return true;
}

/**
 * Arrow down on the last line of a code block that ends the message (arrow up on the first line of one that starts
 * it) makes a paragraph there and moves into it; otherwise there would be no way out with the keyboard. Code never
 * wraps (it scrolls sideways), so its last line is also the last line on screen. With anything after the block,
 * the empty paragraph of an earlier press included, the key is the browser's: there is never a second paragraph.
 */
export function $leaveCodeBlock(direction: 'up' | 'down'): boolean {
  const selection = $getSelection();
  if (!$isRangeSelection(selection) || !selection.isCollapsed()) return false;
  const position = $position(selection.anchor, 'start');
  if (!position || !$isCodeNode(position.top)) return false;
  const { top, index } = position;
  const paragraph = $createParagraphNode();
  if (direction === 'down') {
    if (top.getNextSibling() !== null || index !== lineCount(top) - 1) return false;
    top.insertAfter(paragraph);
  } else {
    if (top.getPreviousSibling() !== null || index !== 0) return false;
    top.insertBefore(paragraph);
  }
  paragraph.select();
  return true;
}

/**
 * Three backticks, typed: at the end of a paragraph they start a code block, at the end of a code block they close it
 * and the caret moves to a paragraph below. A text node transform; only what was just typed at the caret counts.
 */
export function $codeFenceFromText(node: TextNode): void {
  // An input method (a dead-key backtick, as on Turkish keyboards) is still composing this text: replacing the node
  // now would break the composition. The text is looked at again when it is committed.
  if ($getEditor().isComposing()) return;
  const selection = $getSelection();
  if (!$isRangeSelection(selection) || !selection.isCollapsed()) return;
  const { anchor } = selection;
  if (anchor.key !== node.getKey() || anchor.offset !== node.getTextContentSize()) return;
  const text = node.getTextContent();
  if (!text.endsWith(FENCE) || node.getNextSibling() !== null) return;
  const parent = node.getParent();
  const rest = text.slice(0, -FENCE.length);
  if ($isCodeNode(parent)) {
    const paragraph = $createParagraphNode();
    if (rest.length > 0) {
      node.setTextContent(rest);
    } else {
      const previous = node.getPreviousSibling();
      node.remove();
      if ($isLineBreakNode(previous)) previous.remove();
    }
    // Opened and closed with nothing in between: no block at all.
    if (parent.getChildrenSize() === 0) parent.replace(paragraph);
    else parent.insertAfter(paragraph);
    paragraph.select();
    return;
  }
  if (!$isParagraphNode(parent) || !$isRootNode(parent.getParent()) || node.hasFormat('code')) return;
  const code = $createCodeNode();
  if (rest.length === 0 && node.getPreviousSibling() === null) {
    parent.replace(code);
  } else {
    if (rest.length > 0) node.setTextContent(rest);
    else node.remove();
    parent.insertAfter(code);
  }
  code.select();
}

/**
 * A list inside a list item (a paste can bring one) is lifted into the list around it: the schema has no nesting, and
 * the editor shows what will be sent. A list node transform.
 */
export function $flattenNestedList(list: ListNode): void {
  const wrapper = list.getParent();
  if (!$isListItemNode(wrapper)) return;
  let after: LexicalNode = wrapper;
  for (const item of list.getChildren()) {
    after.insertAfter(item);
    after = item;
  }
  if (list.isAttached()) list.remove();
  if (wrapper.isAttached() && wrapper.getChildrenSize() === 0) wrapper.remove();
}

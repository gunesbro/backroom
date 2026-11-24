// The only place where editor nodes and our block JSON meet. The editor's own serialization is never sent or stored.
import { $createCodeNode, $isCodeNode, CodeNode } from '@lexical/code';
import { $createLinkNode, $isLinkNode, LinkNode } from '@lexical/link';
import {
  $createListItemNode,
  $createListNode,
  $isListNode,
  ListItemNode,
  ListNode,
  type ListType,
} from '@lexical/list';
import { $createQuoteNode, $isQuoteNode, QuoteNode } from '@lexical/rich-text';
import {
  $createLineBreakNode,
  $createParagraphNode,
  $createTextNode,
  $getRoot,
  $isElementNode,
  $isLineBreakNode,
  $isTextNode,
  type ElementNode,
  type Klass,
  type LexicalNode,
  type TextFormatType,
} from 'lexical';
import {
  canonicalMarks,
  mergeRuns,
  safeHref,
  type Block,
  type ListItem,
  type Mark,
  type TextNode,
} from './blocks';

/** Every node the editor can hold. Anything else (headings, images, tables) cannot appear or is read as a paragraph. */
export const EDITOR_NODES: readonly Klass<LexicalNode>[] = [
  ListNode,
  ListItemNode,
  LinkNode,
  QuoteNode,
  CodeNode,
];

// Control characters other than tab and newline, and lone surrogates, are refused by the server's text rules.
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/g;
const LONE_SURROGATE = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g;

function cleanText(text: string): string {
  return text.replace(CONTROL, '').replace(LONE_SURROGATE, '').normalize('NFC');
}

const FORMATS: readonly (readonly [TextFormatType, Mark['type']])[] = [
  ['bold', 'bold'],
  ['italic', 'italic'],
  ['strikethrough', 'strike'],
  ['code', 'code'],
];

type Piece = { text: string; marks: Mark[] } | 'break';

function collect(parent: ElementNode, inherited: Mark[], pieces: Piece[]): void {
  for (const child of parent.getChildren()) {
    if ($isLineBreakNode(child)) {
      pieces.push('break');
    } else if ($isTextNode(child)) {
      const marks: Mark[] = [...inherited];
      for (const [format, type] of FORMATS) {
        if (child.hasFormat(format)) marks.push({ type } as Mark);
      }
      pieces.push({ text: cleanText(child.getTextContent()), marks });
    } else if ($isLinkNode(child)) {
      collect(child, [...inherited, { type: 'link', href: child.getURL() }], pieces);
    } else if ($isElementNode(child)) {
      // Unknown inline containers: keep their text.
      collect(child, inherited, pieces);
    }
  }
}

function sameMarkList(a: readonly Mark[], b: readonly Mark[]): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** The text runs of one block, with canonical marks; a line break is `\n` in the neighbouring run. */
function runsOf(element: ElementNode): TextNode[] {
  const pieces: Piece[] = [];
  collect(element, [], pieces);
  const runs: TextNode[] = [];
  const resolved = pieces.map((piece) =>
    piece === 'break' ? piece : { ...piece, marks: canonicalMarks(piece.marks) },
  );
  for (const [index, piece] of resolved.entries()) {
    if (piece === 'break') {
      // A line break inside a mark continues it; between different marks it carries none.
      const before = resolved[index - 1];
      const after = resolved[index + 1];
      const marks =
        before && before !== 'break' && after && after !== 'break' && sameMarkList(before.marks, after.marks)
          ? before.marks
          : [];
      runs.push(marks.length > 0 ? { type: 'text', text: '\n', marks } : { type: 'text', text: '\n' });
    } else {
      runs.push(
        piece.marks.length > 0
          ? { type: 'text', text: piece.text, marks: piece.marks }
          : { type: 'text', text: piece.text },
      );
    }
  }
  return mergeRuns(runs);
}

function listItemsOf(list: ListNode, items: ListItem[]): void {
  for (const item of list.getChildren()) {
    if (!(item instanceof ListItemNode)) continue;
    const nested = item.getChildren().filter((child) => $isListNode(child));
    if (nested.length > 0 && item.getChildrenSize() === nested.length) {
      // A wrapper item that only holds a nested list: nesting is not part of the schema, so the items are flattened.
      for (const inner of nested) listItemsOf(inner, items);
      continue;
    }
    items.push({ type: 'list_item', children: runsOf(item) });
    for (const inner of nested) listItemsOf(inner, items);
  }
}

function isEmptyBlock(block: Block): boolean {
  return block.type === 'paragraph' && block.children.every((run) => run.text.trim().length === 0);
}

/** Reads the blocks from the editor. Call inside `editorState.read` or `editor.read`. */
export function $readBlocks(): Block[] {
  const blocks: Block[] = [];
  for (const node of $getRoot().getChildren()) {
    if ($isListNode(node)) {
      const items: ListItem[] = [];
      listItemsOf(node, items);
      if (items.length > 0) {
        blocks.push({
          type: node.getListType() === 'number' ? 'numbered_list' : 'bulleted_list',
          children: items,
        });
      }
    } else if ($isCodeNode(node)) {
      const children = mergeRuns([{ type: 'text', text: cleanText(node.getTextContent()) }]);
      blocks.push({ type: 'code_block', children });
    } else if ($isQuoteNode(node)) {
      blocks.push({ type: 'quote', children: runsOf(node) });
    } else if ($isElementNode(node)) {
      blocks.push({ type: 'paragraph', children: runsOf(node) });
    }
  }
  // Empty paragraphs at the start and the end are not content.
  while (blocks.length > 0 && isEmptyBlock(blocks[0] as Block)) blocks.shift();
  while (blocks.length > 0 && isEmptyBlock(blocks[blocks.length - 1] as Block)) blocks.pop();
  return blocks;
}

function appendRuns(parent: ElementNode, runs: readonly TextNode[]): void {
  for (const run of runs) {
    const href = run.marks?.find((mark) => mark.type === 'link');
    const target: ElementNode =
      href?.type === 'link' && safeHref(href.href) ? $createLinkNode(safeHref(href.href) ?? '') : parent;
    const lines = run.text.split('\n');
    for (const [index, line] of lines.entries()) {
      if (index > 0) target.append($createLineBreakNode());
      if (line.length === 0) continue;
      const text = $createTextNode(line);
      for (const mark of run.marks ?? []) {
        if (mark.type === 'bold') text.toggleFormat('bold');
        else if (mark.type === 'italic') text.toggleFormat('italic');
        else if (mark.type === 'strike') text.toggleFormat('strikethrough');
        else if (mark.type === 'code') text.toggleFormat('code');
      }
      target.append(text);
    }
    if (target !== parent) parent.append(target);
  }
}

/** Replaces the editor's content with blocks. Call inside `editor.update`. */
export function $writeBlocks(blocks: readonly Block[]): void {
  const root = $getRoot();
  root.clear();
  for (const block of blocks) {
    switch (block.type) {
      case 'paragraph': {
        const paragraph = $createParagraphNode();
        appendRuns(paragraph, block.children);
        root.append(paragraph);
        break;
      }
      case 'quote': {
        const quote = $createQuoteNode();
        appendRuns(quote, block.children);
        root.append(quote);
        break;
      }
      case 'code_block': {
        const code = $createCodeNode();
        for (const [index, line] of block.children
          .map((run) => run.text)
          .join('')
          .split('\n')
          .entries()) {
          if (index > 0) code.append($createLineBreakNode());
          if (line.length > 0) code.append($createTextNode(line));
        }
        root.append(code);
        break;
      }
      case 'bulleted_list':
      case 'numbered_list': {
        const type: ListType = block.type === 'numbered_list' ? 'number' : 'bullet';
        const list = $createListNode(type);
        for (const item of block.children) {
          const listItem = $createListItemNode();
          appendRuns(listItem, item.children);
          list.append(listItem);
        }
        root.append(list);
        break;
      }
    }
  }
  if (root.getChildrenSize() === 0) root.append($createParagraphNode());
}

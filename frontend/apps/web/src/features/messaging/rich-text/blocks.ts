// Our own rich-text JSON (docs/contracts/phase-2.md, "Rich text"): never editor-native, never HTML. This file has no
// editor code, so the message list can render blocks without loading the editor.
import { normalizeMessageText } from '../text';

export type Mark =
  | { type: 'bold' }
  | { type: 'italic' }
  | { type: 'strike' }
  | { type: 'code' }
  | { type: 'link'; href: string };

/** A run of text with the same marks. Line breaks inside a block are `\n` in the text. */
export type TextNode = { type: 'text'; text: string; marks?: Mark[] };

export type ListItem = { type: 'list_item'; children: TextNode[] };

export type Block =
  | { type: 'paragraph'; children: TextNode[] }
  | { type: 'quote'; children: TextNode[] }
  | { type: 'code_block'; children: TextNode[] }
  | { type: 'bulleted_list'; children: ListItem[] }
  | { type: 'numbered_list'; children: ListItem[] };

/** A mention in a received message (the composer does not write them yet). Exactly one member names the target. */
export type MentionNode =
  | { type: 'mention'; accountId: string }
  | { type: 'mention'; groupId: string }
  | { type: 'mention'; special: 'everyone' | 'here' };

/** What a received message can hold inline. */
export type ReceivedRun = TextNode | MentionNode;
export type ReceivedItem = { type: 'list_item'; children: ReceivedRun[] };
export type ReceivedBlock =
  | { type: 'paragraph' | 'quote' | 'code_block'; children: ReceivedRun[] }
  | { type: 'bulleted_list' | 'numbered_list'; children: ReceivedItem[] };

/** The server's limits on a link address and on the JSON of one message. */
export const MAX_HREF_LENGTH = 2048;
export const MAX_BLOCKS_JSON_BYTES = 32 * 1024;

/** At most this many different web addresses in one message (`links.limit`). */
export const MAX_LINKS = 50;

const SAFE_PROTOCOLS = new Set(['http:', 'https:', 'mailto:']);

/**
 * The address if it is safe to link: an absolute `http`, `https` or `mailto` URL of at most 2,048 characters, else
 * null. Everything else (`javascript:`, `data:`, `vbscript:`, `file:`, relative addresses) is refused. The result is
 * the parser's canonical form, so tricks like `java\tscript:` do not get through.
 */
export function safeHref(raw: string): string | null {
  const value = raw.trim();
  if (value.length === 0 || value.length > MAX_HREF_LENGTH) return null;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return null;
  }
  if (!SAFE_PROTOCOLS.has(url.protocol)) return null;
  const href = url.href;
  return href.length <= MAX_HREF_LENGTH ? href : null;
}

/** What a person typed in the link field: `example.com` gets `https://`; an address with another scheme is refused. */
export function hrefFromInput(raw: string): string | null {
  const value = raw.trim();
  if (value.length === 0) return null;
  const direct = safeHref(value);
  if (direct) return direct;
  // No scheme at all (`example.com/path`, `example.com:8080`): assume https. `javascript:alert(1)` has a scheme.
  if (/^[a-z][a-z\d+.-]*:(?!\d)/i.test(value) || value.startsWith('//')) return null;
  return safeHref(`https://${value}`);
}

const MARK_ORDER = ['bold', 'italic', 'strike', 'code', 'link'] as const;

/** Marks in the canonical order, one of each type; a link with an unsafe address loses its mark. */
export function canonicalMarks(marks: readonly Mark[]): Mark[] {
  const result: Mark[] = [];
  for (const type of MARK_ORDER) {
    const mark = marks.find((candidate) => candidate.type === type);
    if (!mark) continue;
    if (mark.type === 'link') {
      const href = safeHref(mark.href);
      if (href) result.push({ type: 'link', href });
    } else {
      result.push(mark);
    }
  }
  return result;
}

function sameMarks(a: readonly Mark[] | undefined, b: readonly Mark[] | undefined): boolean {
  const left = a ?? [];
  const right = b ?? [];
  return (
    left.length === right.length &&
    left.every((mark, index) => {
      const other = right[index];
      return (
        other?.type === mark.type &&
        (mark.type !== 'link' || (other.type === 'link' && other.href === mark.href))
      );
    })
  );
}

/** Drops empty runs and joins neighbours with equal marks, so equal content has one shape. */
export function mergeRuns(runs: readonly TextNode[]): TextNode[] {
  const result: TextNode[] = [];
  for (const run of runs) {
    if (run.text.length === 0) continue;
    const last = result[result.length - 1];
    if (last && sameMarks(last.marks, run.marks)) {
      result[result.length - 1] = { ...last, text: last.text + run.text };
    } else {
      result.push(run);
    }
  }
  return result;
}

function runsText(runs: readonly TextNode[]): string {
  return runs.map((run) => run.text).join('');
}

/**
 * The plain `text` of blocks, as the server derives it: blocks and list items on their own lines, no markers; the
 * result normalized like any message text. The composer counts the 4,000 characters on this.
 */
export function blocksToText(blocks: readonly Block[]): string {
  const lines: string[] = [];
  for (const block of blocks) {
    if (block.type === 'bulleted_list' || block.type === 'numbered_list') {
      for (const item of block.children) lines.push(runsText(item.children));
    } else {
      lines.push(runsText(block.children));
    }
  }
  return normalizeMessageText(lines.join('\n'));
}

/** How many different `http` and `https` addresses the blocks link to, as the server counts them (mailto is not). */
export function distinctWebLinks(blocks: readonly Block[]): number {
  const hrefs = new Set<string>();
  const visit = (runs: readonly TextNode[]) => {
    for (const run of runs) {
      for (const mark of run.marks ?? []) {
        if (mark.type === 'link' && !mark.href.startsWith('mailto:')) hrefs.add(mark.href);
      }
    }
  };
  for (const block of blocks) {
    if (block.type === 'bulleted_list' || block.type === 'numbered_list') {
      for (const item of block.children) visit(item.children);
    } else {
      visit(block.children);
    }
  }
  return hrefs.size;
}

/** The JSON size the server limits (UTF-8 bytes). */
export function blocksJsonBytes(blocks: readonly Block[]): number {
  return new TextEncoder().encode(JSON.stringify(blocks)).length;
}

// Reading what the server sent. It is data from outside: everything is checked, unknown types are skipped, and
// nothing reaches the page as markup.

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function parseMarks(value: unknown): Mark[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const marks: Mark[] = [];
  for (const raw of value as unknown[]) {
    if (!isRecord(raw)) continue;
    switch (raw.type) {
      case 'bold':
      case 'italic':
      case 'strike':
      case 'code':
        marks.push({ type: raw.type });
        break;
      case 'link':
        if (typeof raw.href === 'string') marks.push({ type: 'link', href: raw.href });
        break;
      default:
        break; // an unknown mark is skipped
    }
  }
  const canonical = canonicalMarks(marks);
  return canonical.length > 0 ? canonical : undefined;
}

function parseMention(raw: Record<string, unknown>): MentionNode | undefined {
  if (typeof raw.accountId === 'string') return { type: 'mention', accountId: raw.accountId };
  if (typeof raw.groupId === 'string') return { type: 'mention', groupId: raw.groupId };
  if (raw.special === 'everyone' || raw.special === 'here') return { type: 'mention', special: raw.special };
  return undefined;
}

function parseRuns(value: unknown): ReceivedRun[] {
  if (!Array.isArray(value)) return [];
  const runs: ReceivedRun[] = [];
  for (const raw of value as unknown[]) {
    if (isRecord(raw) && raw.type === 'mention') {
      const mention = parseMention(raw);
      if (mention) runs.push(mention);
      continue;
    }
    if (!isRecord(raw) || raw.type !== 'text' || typeof raw.text !== 'string') continue; // unknown nodes
    const marks = parseMarks(raw.marks);
    runs.push(marks ? { type: 'text', text: raw.text, marks } : { type: 'text', text: raw.text });
  }
  return runs;
}

/**
 * The blocks to show for a received message, or null when there is nothing to render (not an array, or no known
 * block). Unknown block types, unknown inline nodes and unknown marks are skipped, never shown as raw data.
 */
export function parseBlocks(value: unknown): ReceivedBlock[] | null {
  if (!Array.isArray(value)) return null;
  const blocks: ReceivedBlock[] = [];
  for (const raw of value as unknown[]) {
    if (!isRecord(raw)) continue;
    switch (raw.type) {
      case 'paragraph':
      case 'quote':
      case 'code_block':
        blocks.push({ type: raw.type, children: parseRuns(raw.children) });
        break;
      case 'bulleted_list':
      case 'numbered_list': {
        const items: ReceivedItem[] = [];
        if (Array.isArray(raw.children)) {
          for (const item of raw.children as unknown[]) {
            if (isRecord(item) && item.type === 'list_item') {
              items.push({ type: 'list_item', children: parseRuns(item.children) });
            }
          }
        }
        blocks.push({ type: raw.type, children: items });
        break;
      }
      default:
        break; // an unknown block type is skipped
    }
  }
  return blocks.length > 0 ? blocks : null;
}

/**
 * The names the server put into the derived `text` for the mentions of received blocks (`@` and the name it looked up),
 * for a mention whose person this client does not know. The text is walked in step with the blocks: each text run is
 * found again after the previous one, and a mention's name is what lies between its `@` and the next run. A name that
 * cannot be found (the text was normalized differently) is simply missing. Never throws.
 */
export function derivedMentionNames(
  blocks: readonly ReceivedBlock[],
  text: string,
): Map<MentionNode, string> {
  const flat: ReceivedRun[] = [];
  for (const block of blocks) {
    for (const child of block.children) {
      if (child.type === 'list_item') flat.push(...child.children);
      else flat.push(child);
    }
  }
  const names = new Map<MentionNode, string>();
  let cursor = 0;
  for (const [index, run] of flat.entries()) {
    if (run.type === 'text') {
      const at = text.indexOf(run.text, cursor);
      if (at === -1) return names;
      cursor = at + run.text.length;
      continue;
    }
    const at = text.indexOf('@', cursor);
    if (at === -1) return names;
    const next = flat[index + 1];
    const lineEnd = text.indexOf('\u000A', at);
    let end = lineEnd === -1 ? text.length : lineEnd;
    if (next?.type === 'text') {
      const anchor = text.indexOf(next.text, at + 1);
      if (anchor !== -1 && anchor < end) end = anchor;
    } else if (next?.type === 'mention') {
      const anchor = text.indexOf('@', at + 1);
      if (anchor !== -1 && anchor < end) end = anchor;
    }
    const name = text.slice(at + 1, end).trim();
    if (name.length > 0) names.set(run, name);
    cursor = end;
  }
  return names;
}

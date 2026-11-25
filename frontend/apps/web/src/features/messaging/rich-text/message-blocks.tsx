import type { MouseEvent, ReactNode } from 'react';
import { isDesktopApp, tauriInvoke } from '@/features/auth';
import {
  derivedMentionNames,
  parseBlocks,
  safeHref,
  type MentionNode,
  type ReceivedBlock,
  type ReceivedRun,
  type TextNode,
} from './blocks';

/** How a mention is named: the client's own member data first, then what the server put into the plain text. */
type Names = {
  account: (accountId: string) => string | undefined;
  derived: ReadonlyMap<MentionNode, string>;
};

function mentionLabel(node: MentionNode, names: Names): string {
  const known = 'accountId' in node ? names.account(node.accountId) : undefined;
  const label = known ?? names.derived.get(node) ?? ('special' in node ? node.special : '');
  return `@${label}`;
}

/**
 * In the desktop app a link goes to the system browser through the shell's opener plugin (capability scoped to http,
 * https and mailto), never into a new webview window. On the web the anchor opens a tab with `noopener noreferrer`.
 */
function openInSystemBrowser(event: MouseEvent<HTMLAnchorElement>, href: string): void {
  event.preventDefault();
  tauriInvoke('plugin:opener|open_url', { url: href }).catch(() => {
    // Nothing to show: the link simply does not open.
  });
}

function textRun(node: TextNode, key: number): ReactNode {
  let content: ReactNode = node.text;
  let href: string | null = null;
  const desktop = isDesktopApp();
  for (const mark of node.marks ?? []) {
    switch (mark.type) {
      case 'bold':
        content = <strong>{content}</strong>;
        break;
      case 'italic':
        content = <em>{content}</em>;
        break;
      case 'strike':
        content = <s>{content}</s>;
        break;
      case 'code':
        content = <code className="bm-inline-code">{content}</code>;
        break;
      case 'link':
        href = safeHref(mark.href);
        break;
    }
  }
  if (href) {
    content = (
      <a
        href={href}
        // The real address is always there to read (hover, long press, screen readers), whatever the text says.
        title={href}
        {...(desktop ? {} : { target: '_blank' })}
        rel="noopener noreferrer"
        onClick={
          desktop
            ? (event) => {
                openInSystemBrowser(event, href);
              }
            : undefined
        }
        onAuxClick={
          desktop
            ? (event) => {
                openInSystemBrowser(event, href);
              }
            : undefined
        }
      >
        {/* Link text may carry bidirectional controls: it is isolated, so it cannot reorder what is around it. */}
        <bdi>{content}</bdi>
      </a>
    );
  }
  return <span key={key}>{content}</span>;
}

function runs(nodes: readonly ReceivedRun[], names: Names): ReactNode[] {
  return nodes.map((node, key) =>
    node.type === 'mention' ? (
      // A chip with the person's name: React escapes it, and it is not a link or a field.
      <span key={key} className="bm-mention" data-mention="">
        <bdi>{mentionLabel(node, names)}</bdi>
      </span>
    ) : (
      textRun(node, key)
    ),
  );
}

function plainText(nodes: readonly ReceivedRun[]): string {
  return nodes.map((node) => (node.type === 'text' ? node.text : '')).join('');
}

function renderBlock(block: ReceivedBlock, key: number, names: Names): ReactNode {
  switch (block.type) {
    case 'paragraph':
      return (
        <p key={key} className="bm-text">
          {runs(block.children, names)}
        </p>
      );
    case 'quote':
      return (
        <blockquote key={key} className="bm-text bm-quote">
          {runs(block.children, names)}
        </blockquote>
      );
    case 'code_block':
      return (
        <pre key={key} className="bm-code-block">
          <code>{plainText(block.children)}</code>
        </pre>
      );
    case 'bulleted_list':
    case 'numbered_list': {
      const items = block.children.map((item, index) => <li key={index}>{runs(item.children, names)}</li>);
      return block.type === 'bulleted_list' ? (
        <ul key={key} className="bm-text bm-list">
          {items}
        </ul>
      ) : (
        <ol key={key} className="bm-text bm-list">
          {items}
        </ol>
      );
    }
  }
}

/**
 * A message body from its blocks. Only React elements and text are produced (never markup strings), links keep
 * http, https and mailto addresses and open with `rel="noopener noreferrer"`, and types this client does not know
 * are skipped. With no renderable block the plain `text` is shown, as before.
 */
export function MessageBody({
  blocks,
  text,
  sending,
  mentionName,
}: {
  blocks: unknown;
  text: string;
  sending: boolean;
  /** The display name of a member the client knows, or undefined (the name in `text` is shown then). */
  mentionName?: (accountId: string) => string | undefined;
}) {
  const parsed = parseBlocks(blocks);
  const className = sending ? 'bm-body bm-body--sending' : 'bm-body';
  if (!parsed) return <p className={sending ? 'bm-text bm-text--sending' : 'bm-text'}>{text}</p>;
  const names: Names = {
    account: mentionName ?? (() => undefined),
    derived: derivedMentionNames(parsed, text),
  };
  return <div className={className}>{parsed.map((block, key) => renderBlock(block, key, names))}</div>;
}

import type { MouseEvent, ReactNode } from 'react';
import { isDesktopApp, tauriInvoke } from '@/features/auth';
import { parseBlocks, safeHref, type Block, type TextNode } from './blocks';

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

function run(node: TextNode, key: number): ReactNode {
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
        {content}
      </a>
    );
  }
  return <span key={key}>{content}</span>;
}

function runs(nodes: readonly TextNode[]): ReactNode[] {
  return nodes.map(run);
}

function renderBlock(block: Block, key: number): ReactNode {
  switch (block.type) {
    case 'paragraph':
      return (
        <p key={key} className="bm-text">
          {runs(block.children)}
        </p>
      );
    case 'quote':
      return (
        <blockquote key={key} className="bm-text bm-quote">
          {runs(block.children)}
        </blockquote>
      );
    case 'code_block':
      return (
        <pre key={key} className="bm-code-block">
          <code>{block.children.map((node) => node.text).join('')}</code>
        </pre>
      );
    case 'bulleted_list':
    case 'numbered_list': {
      const items = block.children.map((item, index) => <li key={index}>{runs(item.children)}</li>);
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
export function MessageBody({ blocks, text, sending }: { blocks: unknown; text: string; sending: boolean }) {
  const parsed = parseBlocks(blocks);
  const className = sending ? 'bm-body bm-body--sending' : 'bm-body';
  if (!parsed) return <p className={sending ? 'bm-text bm-text--sending' : 'bm-text'}>{text}</p>;
  return <div className={className}>{parsed.map(renderBlock)}</div>;
}

import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { MessageBody } from './message-blocks';

const run = (text: string, marks?: object[]) => ({ type: 'text', text, ...(marks ? { marks } : {}) });

describe('MessageBody rendering', () => {
  it('Render_WhenEveryBlockAndMarkType_ProducesTheMatchingElements', () => {
    const blocks = [
      {
        type: 'paragraph',
        children: [
          run('b', [{ type: 'bold' }]),
          run('i', [{ type: 'italic' }]),
          run('s', [{ type: 'strike' }]),
          run('c', [{ type: 'code' }]),
        ],
      },
      { type: 'quote', children: [run('quoted')] },
      { type: 'code_block', children: [run('line1\nline2')] },
      {
        type: 'bulleted_list',
        children: [
          { type: 'list_item', children: [run('one')] },
          { type: 'list_item', children: [run('two')] },
        ],
      },
      { type: 'numbered_list', children: [{ type: 'list_item', children: [run('first')] }] },
    ];
    const { container } = render(<MessageBody text="plain" sending={false} blocks={blocks} />);

    expect(container.querySelector('strong')?.textContent).toBe('b');
    expect(container.querySelector('em')?.textContent).toBe('i');
    expect(container.querySelector('s')?.textContent).toBe('s');
    expect(container.querySelector('code.bm-inline-code')?.textContent).toBe('c');
    expect(container.querySelector('blockquote')?.textContent).toBe('quoted');
    expect(container.querySelector('pre code')?.textContent).toBe('line1\nline2');
    expect(container.querySelectorAll('ul > li')).toHaveLength(2);
    expect(container.querySelectorAll('ol > li')).toHaveLength(1);
    expect(screen.queryByText('plain')).toBeNull();
  });

  it('Render_WhenNoRenderableBlock_FallsBackToText', () => {
    render(<MessageBody text="plain text" sending={false} blocks={[{ type: 'hologram' }]} />);

    expect(screen.getByText('plain text')).toBeTruthy();
  });

  it('Render_WhenTextHasMarkup_ShowsItAsTextNeverAsElements', () => {
    const blocks = [{ type: 'paragraph', children: [run('<img src=x onerror=alert(1)>')] }];
    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    expect(container.querySelector('img')).toBeNull();
    expect(container.textContent).toBe('<img src=x onerror=alert(1)>');
  });

  it('Render_WhenLink_OpensInANewTabWithTheAddressShownAndTheTextIsolated', () => {
    const blocks = [
      {
        type: 'paragraph',
        children: [run('‮gnp.exe', [{ type: 'link', href: 'https://example.com/a' }])],
      },
    ];
    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    const link = screen.getByRole('link');
    expect(link).toHaveAttribute('href', 'https://example.com/a');
    expect(link).toHaveAttribute('title', 'https://example.com/a');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(container.querySelector('a > bdi')?.textContent).toBe('‮gnp.exe');
  });

  it('Render_WhenLinkIsUnsafe_ShowsTheTextWithoutALink', () => {
    const blocks = [
      { type: 'paragraph', children: [run('click', [{ type: 'link', href: 'javascript:alert(1)' }])] },
    ];
    render(<MessageBody text="" sending={false} blocks={blocks} />);

    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText('click')).toBeTruthy();
  });

  it('Render_WhenMentionNode_SkipsItAndKeepsTheText', () => {
    const blocks = [
      { type: 'paragraph', children: [run('hi '), { type: 'mention', accountId: 'a-1' }, run('there')] },
    ];
    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    expect(container.textContent).toBe('hi there');
  });
});

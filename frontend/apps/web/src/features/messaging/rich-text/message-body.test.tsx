import { render, screen } from '@testing-library/react';
import axe from 'axe-core';
import { describe, expect, it } from 'vitest';
import { LocaleProvider } from '@/shared/locale-provider';
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
        children: [run('\u202Egnp.exe', [{ type: 'link', href: 'https://example.com/a' }])],
      },
    ];
    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    const link = screen.getByRole('link');
    expect(link).toHaveAttribute('href', 'https://example.com/a');
    expect(link).toHaveAttribute('title', 'https://example.com/a');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(container.querySelector('a > bdi')?.textContent).toBe('\u202Egnp.exe');
  });

  it('Render_WhenLinkIsUnsafe_ShowsTheTextWithoutALink', () => {
    const blocks = [
      { type: 'paragraph', children: [run('click', [{ type: 'link', href: 'javascript:alert(1)' }])] },
    ];
    render(<MessageBody text="" sending={false} blocks={blocks} />);

    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText('click')).toBeTruthy();
  });

  it('Render_WhenMentionOfAKnownMember_ShowsAChipWithTheirName', () => {
    const blocks = [
      { type: 'paragraph', children: [run('hi '), { type: 'mention', accountId: 'a-1' }, run(' there')] },
    ];
    const { container } = render(
      <MessageBody
        text="hi @Old Name there"
        sending={false}
        blocks={blocks}
        mentionName={(id) => (id === 'a-1' ? 'Bob' : undefined)}
      />,
    );

    expect(container.querySelector('[data-mention]')?.textContent).toBe('@Bob');
    expect(container.textContent).toBe('hi @Bob there');
    expect(container.querySelector('a, input, textarea')).toBeNull();
  });

  it('Render_WhenMentionOfAnUnknownMember_UsesTheNameInTheText', () => {
    const blocks = [
      { type: 'paragraph', children: [run('hi '), { type: 'mention', accountId: 'a-2' }, run(' there')] },
    ];
    const { container } = render(
      <MessageBody text="hi @Carol Q there" sending={false} blocks={blocks} mentionName={() => undefined} />,
    );

    expect(container.querySelector('[data-mention]')?.textContent).toBe('@Carol Q');
  });

  it('Render_WhenMentionNameIsMarkup_ShowsItAsText', () => {
    const blocks = [{ type: 'paragraph', children: [{ type: 'mention', accountId: 'a-3' }] }];
    const { container } = render(
      <MessageBody text="@x" sending={false} blocks={blocks} mentionName={() => '<b>x</b>'} />,
    );

    expect(container.querySelector('b')).toBeNull();
    expect(container.textContent).toBe('@<b>x</b>');
  });

  it('Render_WhenMentionHasNoKnownName_ShowsANeutralLabelNotTheNextRunsText', () => {
    const blocks = [
      {
        type: 'paragraph',
        children: [{ type: 'mention', accountId: 'a-4' }, { type: 'mention', accountId: 'a-5' }, run(' end')],
      },
    ];
    const { container } = render(
      <MessageBody text="@Ann Bob end" sending={false} blocks={blocks} mentionName={() => undefined} />,
    );

    expect([...container.querySelectorAll('[data-mention]')].map((chip) => chip.textContent)).toEqual([
      '@\u2026',
      '@\u2026',
    ]);
  });

  it('Render_WhenTextIsNotAString_DoesNotCrash', () => {
    const { container } = render(
      <MessageBody text={undefined as unknown as string} sending={false} blocks={undefined} />,
    );

    expect(container.textContent).toBe('');
  });

  it('Render_WhenMentionNodeHasNoNameAnywhere_ShowsAnAtSignAndKeepsTheText', () => {
    const blocks = [
      { type: 'paragraph', children: [run('hi '), { type: 'mention', accountId: 'a-1' }, run('there')] },
    ];
    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    expect(container.textContent).toBe('hi @…there');
  });
});

describe('MessageBody code block', () => {
  // The box scrolls sideways, so it must take the keyboard (WebKit does not focus scrollers by itself) and say what
  // it is. The scrolling itself needs layout: apps/web/e2e runs axe's scrollable-region rule in a real browser.
  it.each([
    ['en', 'Code block'],
    ['tr', 'Kod bloğu'],
  ] as const)('Render_WhenACodeBlockAndTheLanguageIs%s_IsANamedFocusableRegion', async (locale, name) => {
    const { container } = render(
      <LocaleProvider locale={locale}>
        <MessageBody
          text="plain"
          sending={false}
          blocks={[{ type: 'code_block', children: [run('const a = 1;')] }]}
        />
      </LocaleProvider>,
    );

    const region = screen.getByRole('region', { name });
    expect(region.tagName).toBe('PRE');
    expect(region).toHaveAttribute('tabindex', '0');
    region.focus();
    expect(region).toHaveFocus();
    const results = await axe.run(container, {
      rules: { 'color-contrast': { enabled: false }, region: { enabled: false } },
    });
    expect(results.violations.map((violation) => `${violation.id}: ${violation.help}`)).toEqual([]);
  });
});

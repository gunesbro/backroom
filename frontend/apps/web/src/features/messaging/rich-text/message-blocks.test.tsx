import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MessageBody } from './message-blocks';

const invoke = vi.fn<(command: string, args?: Record<string, unknown>) => Promise<unknown>>();
vi.mock('@tauri-apps/api/core', () => ({
  invoke: (command: string, args?: Record<string, unknown>) => invoke(command, args),
}));

const linkBlocks = [
  {
    type: 'paragraph',
    children: [{ type: 'text', text: 'site', marks: [{ type: 'link', href: 'https://example.com/x' }] }],
  },
];

describe('MessageBody in the desktop app', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    invoke.mockReset();
  });

  it('Click_WhenDesktop_OpensTheSystemBrowserAndNoNewWindow', async () => {
    vi.stubGlobal('isTauri', true);
    invoke.mockResolvedValue(undefined);
    render(<MessageBody text="" sending={false} blocks={linkBlocks} />);
    const link = screen.getByRole('link', { name: 'site' });
    expect(link).not.toHaveAttribute('target');

    const notPrevented = fireEvent.click(link);

    expect(notPrevented).toBe(false);
    await vi.waitFor(() => {
      expect(invoke).toHaveBeenCalledExactlyOnceWith('plugin:opener|open_url', {
        url: 'https://example.com/x',
      });
    });
  });

  it('Click_WhenWeb_LeavesTheAnchorToTheBrowser', () => {
    render(<MessageBody text="" sending={false} blocks={linkBlocks} />);
    const link = screen.getByRole('link', { name: 'site' });
    expect(link).toHaveAttribute('target', '_blank');

    const notPrevented = fireEvent.click(link);

    expect(notPrevented).toBe(true);
    expect(invoke).not.toHaveBeenCalled();
  });
});

describe('MessageBody', () => {
  it('MessageBody_WhenLinkBlocks_RendersAnchorWithNoopener', () => {
    render(
      <MessageBody
        text="fallback"
        sending={false}
        blocks={[
          {
            type: 'paragraph',
            children: [
              {
                type: 'text',
                text: 'site',
                marks: [{ type: 'bold' }, { type: 'link', href: 'https://example.com/x' }],
              },
            ],
          },
        ]}
      />,
    );
    const link = screen.getByRole('link', { name: 'site' });
    expect(link).toHaveAttribute('href', 'https://example.com/x');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link.querySelector('strong')).not.toBeNull();
  });

  it.each(['javascript:alert(1)', 'data:text/html,x', 'vbscript:x', 'file:///c:/x'])(
    'MessageBody_WhenLinkHrefIsUnsafe_RendersNoAnchor (%s)',
    (href) => {
      render(
        <MessageBody
          text="fallback"
          sending={false}
          blocks={[
            {
              type: 'paragraph',
              children: [{ type: 'text', text: 'click me', marks: [{ type: 'link', href }] }],
            },
          ]}
        />,
      );
      expect(screen.queryByRole('link')).toBeNull();
      expect(screen.getByText('click me')).toBeInTheDocument();
    },
  );

  it('MessageBody_WhenMarkupInText_ShowsItAsText', () => {
    const { container } = render(
      <MessageBody
        text=""
        sending={false}
        blocks={[
          { type: 'paragraph', children: [{ type: 'text', text: '<img src=x onerror=alert(1)><b>hi</b>' }] },
        ]}
      />,
    );
    expect(container.querySelector('img')).toBeNull();
    expect(container.querySelector('b')).toBeNull();
    expect(container).toHaveTextContent('<img src=x onerror=alert(1)><b>hi</b>');
  });

  it('MessageBody_WhenUnknownBlockTypes_SkipsThemAndKeepsKnownOnes', () => {
    render(
      <MessageBody
        text="fallback"
        sending={false}
        blocks={[
          { type: 'carousel', children: [{ type: 'text', text: 'unknown block' }] },
          { type: 'quote', children: [{ type: 'text', text: 'quoted' }] },
          {
            type: 'numbered_list',
            children: [{ type: 'list_item', children: [{ type: 'text', text: 'first' }] }],
          },
          { type: 'code_block', children: [{ type: 'text', text: 'let a = 1;' }] },
        ]}
      />,
    );
    expect(screen.queryByText('unknown block')).toBeNull();
    expect(screen.getByText('quoted').closest('blockquote')).not.toBeNull();
    expect(screen.getByRole('listitem')).toHaveTextContent('first');
    expect(screen.getByText('let a = 1;').closest('pre')).not.toBeNull();
  });

  it('Emoji_InText_AreImages_AndInCodeTheyStayCharacters', () => {
    const blocks = [
      {
        type: 'paragraph',
        children: [
          { type: 'text', text: 'Shipped 🎉 ' },
          { type: 'text', text: 'with 👍🏽', marks: [{ type: 'bold' }] },
          { type: 'text', text: 'log("🚀")', marks: [{ type: 'code' }] },
        ],
      },
      { type: 'code_block', children: [{ type: 'text', text: 'print("🔥")' }] },
    ];

    const { container } = render(<MessageBody text="" sending={false} blocks={blocks} />);

    const images = screen.getAllByRole('img');
    expect(images.map((image) => image.getAttribute('alt'))).toEqual(['🎉', '👍🏽']);
    expect(images[1]?.closest('strong')).not.toBeNull();
    for (const image of images) expect(image).not.toHaveClass('be-emoji--large');
    expect(screen.getByText('log("🚀")')).toBeInTheDocument();
    expect(screen.getByText('print("🔥")')).toBeInTheDocument();
    expect(container).toHaveTextContent('Shipped with');
  });

  it('Message_OfOnlyAFewEmoji_ShowsThemLarge', () => {
    const only = [{ type: 'paragraph', children: [{ type: 'text', text: '🎉 👍🏽' }] }];
    const { unmount } = render(<MessageBody text="" sending={false} blocks={only} />);
    for (const image of screen.getAllByRole('img')) expect(image).toHaveClass('be-emoji--large');
    unmount();

    // Not in a list, and not next to words.
    const list = [
      { type: 'bulleted_list', children: [{ type: 'list_item', children: [{ type: 'text', text: '🎉' }] }] },
    ];
    render(<MessageBody text="" sending={false} blocks={list} />);
    expect(screen.getByRole('img')).not.toHaveClass('be-emoji--large');
  });

  it('PlainText_WithEmoji_ShowsImages_LargeWhenThereIsNothingElse', () => {
    const { unmount } = render(<MessageBody text="ok 😀" sending={false} blocks={null} />);
    expect(screen.getByRole('img', { name: '😀' })).not.toHaveClass('be-emoji--large');
    expect(screen.getByText('ok')).toBeInTheDocument();
    unmount();

    render(<MessageBody text="😀" sending blocks={null} />);
    expect(screen.getByRole('img', { name: '😀' })).toHaveClass('be-emoji--large');
  });

  it('MessageBody_WhenNoRenderableBlocks_ShowsPlainText', () => {
    render(<MessageBody text="plain words" sending={false} blocks={[{ type: 'carousel' }]} />);
    expect(screen.getByText('plain words')).toBeInTheDocument();
  });
});

import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { EmojiImage, EmojiText } from './emoji-image';
import { forgetMissingEmojiImages } from './missing-images';

// A joined sequence no image set has.
const UNKNOWN = '🚀\u{200D}🧀';

afterEach(() => {
  forgetMissingEmojiImages();
  vi.useRealTimers();
});

describe('EmojiImage', () => {
  it('Emoji_IsAnImageThatStillCarriesItsCharacter', () => {
    render(<EmojiImage emoji="👍🏽" label="thumbs up" />);

    const image = screen.getByRole('img', { name: '👍🏽' });
    expect(image).toHaveAttribute('src', '/assets/emoji-test/1f44d-1f3fd.svg');
    expect(image).toHaveAttribute('alt', '👍🏽');
    expect(image).toHaveAttribute('title', 'thumbs up');
    expect(image).toHaveAttribute('draggable', 'false');
    expect(image).toHaveAttribute('decoding', 'async');
    expect(image).toHaveClass('be-emoji');
    expect(image).not.toHaveClass('be-emoji--large');
  });

  it('Image_LoadsAtOnce_AndWaitsUntilItIsNearTheScreenOnlyWhereAsked', () => {
    // A list that only holds what is on screen (messages) must not wait a second time.
    const { unmount } = render(<EmojiImage emoji="🚀" />);
    expect(screen.getByRole('img')).not.toHaveAttribute('loading');
    unmount();

    render(<EmojiImage emoji="🚀" lazy />);
    expect(screen.getByRole('img')).toHaveAttribute('loading', 'lazy');
  });

  it('Name_WithoutALabel_ComesFromTheEmojiDataWhenThePointerIsOnIt', async () => {
    render(<EmojiImage emoji="🚀" />);
    const image = screen.getByRole('img', { name: '🚀' });
    expect(image).not.toHaveAttribute('title');

    fireEvent.pointerEnter(image);

    expect(await screen.findByTitle('rocket')).toBe(image);
  });

  it('Tooltip_WhenOff_ShowsNoNameAtAll', () => {
    render(<EmojiImage emoji="🚀" label="rocket" tooltip={false} />);
    const image = screen.getByRole('img', { name: '🚀' });

    fireEvent.pointerEnter(image);

    expect(image).not.toHaveAttribute('title');
  });

  it('Emoji_WhoseImageCannotBeLoaded_IsShownAsText_AlsoTheNextTime', () => {
    const { container, unmount } = render(
      <p>
        <EmojiImage emoji={UNKNOWN} />
      </p>,
    );

    fireEvent.error(screen.getByRole('img'));

    expect(screen.queryByRole('img')).toBeNull();
    expect(container).toHaveTextContent(UNKNOWN);

    unmount();
    render(<EmojiImage emoji={UNKNOWN} />);
    expect(screen.queryByRole('img')).toBeNull();
  });

  it('MissingImage_IsTriedAgain_AfterFiveMinutes', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-11-25T10:00:00Z'));
    const { unmount } = render(<EmojiImage emoji="😀" />);
    fireEvent.error(screen.getByRole('img'));
    unmount();

    vi.setSystemTime(new Date('2026-11-25T10:04:59Z'));
    const early = render(<EmojiImage emoji="😀" />);
    expect(screen.queryByRole('img')).toBeNull();
    early.unmount();

    vi.setSystemTime(new Date('2026-11-25T10:05:00Z'));
    render(<EmojiImage emoji="😀" />);
    expect(screen.getByRole('img', { name: '😀' })).toBeInTheDocument();
  });

  it('MissingImages_AreTriedAgain_WhenTheConnectionIsBack', () => {
    // Without a connection every image fails; that must not last once it is back.
    const { unmount } = render(<EmojiImage emoji="😀" />);
    fireEvent.error(screen.getByRole('img'));
    unmount();

    window.dispatchEvent(new Event('online'));

    render(<EmojiImage emoji="😀" />);
    expect(screen.getByRole('img', { name: '😀' })).toBeInTheDocument();
  });

  it('MissingImages_AreRememberedForFiveHundredEmojiAtMost_TheOldestGoFirst', () => {
    const emoji = (index: number) => String.fromCodePoint(0x1f300 + index);
    const fail = (value: string) => {
      const { unmount } = render(<EmojiImage emoji={value} />);
      fireEvent.error(screen.getByRole('img'));
      unmount();
    };

    for (let index = 0; index <= 500; index++) fail(emoji(index));

    // The first one was forgotten to make room, so it tries its image again; the last one is remembered.
    const first = render(<EmojiImage emoji={emoji(0)} />);
    expect(screen.getByRole('img')).toBeInTheDocument();
    first.unmount();
    render(<EmojiImage emoji={emoji(500)} />);
    expect(screen.queryByRole('img')).toBeNull();
  });
});

describe('EmojiText', () => {
  it('Text_KeepsItsWords_AndShowsItsEmojiAsImages', () => {
    const { container } = render(
      <p>
        <EmojiText text="Done 🎉 and 🇹🇷!" />
      </p>,
    );

    expect(container.querySelector('p')?.childNodes).toHaveLength(5);
    expect(container).toHaveTextContent('Done and !');
    const images = screen.getAllByRole('img');
    expect(images.map((image) => image.getAttribute('alt'))).toEqual(['🎉', '🇹🇷']);
    for (const image of images) expect(image).not.toHaveAttribute('loading');
  });

  it('Text_WithoutEmoji_IsJustText', () => {
    const { container } = render(
      <p>
        <EmojiText text="I ❤ plain text ©" />
      </p>,
    );

    expect(container.querySelector('p')?.innerHTML).toBe('I ❤ plain text ©');
  });

  it('Large_MakesEveryEmojiLarge', () => {
    render(<EmojiText text="🎉 👍🏽" large />);

    for (const image of screen.getAllByRole('img')) expect(image).toHaveClass('be-emoji--large');
  });
});

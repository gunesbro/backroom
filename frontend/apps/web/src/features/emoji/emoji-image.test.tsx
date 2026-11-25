import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { EmojiImage, EmojiText } from './emoji-image';

describe('EmojiImage', () => {
  it('Emoji_IsAnImageThatStillCarriesItsCharacter', () => {
    render(<EmojiImage emoji="👍🏽" label="thumbs up" />);

    const image = screen.getByRole('img', { name: '👍🏽' });
    expect(image).toHaveAttribute('src', '/assets/emoji-test/1f44d-1f3fd.svg');
    expect(image).toHaveAttribute('alt', '👍🏽');
    expect(image).toHaveAttribute('title', 'thumbs up');
    expect(image).toHaveAttribute('draggable', 'false');
    expect(image).toHaveAttribute('loading', 'lazy');
    expect(image).toHaveAttribute('decoding', 'async');
    expect(image).toHaveClass('be-emoji');
    expect(image).not.toHaveClass('be-emoji--large');
  });

  it('Name_WithoutALabel_ComesFromTheEmojiDataWhenThePointerIsOnIt', async () => {
    render(<EmojiImage emoji="🚀" />);
    const image = screen.getByRole('img', { name: '🚀' });
    expect(image).not.toHaveAttribute('title');

    fireEvent.pointerEnter(image);

    expect(await screen.findByTitle('rocket')).toBe(image);
  });

  it('Emoji_WhoseImageCannotBeLoaded_IsShownAsText_AlsoTheNextTime', () => {
    // A joined sequence no image set has.
    const unknown = '🚀\u200D🧀';
    const { container, unmount } = render(
      <p>
        <EmojiImage emoji={unknown} />
      </p>,
    );

    fireEvent.error(screen.getByRole('img'));

    expect(screen.queryByRole('img')).toBeNull();
    expect(container).toHaveTextContent(unknown);

    unmount();
    render(<EmojiImage emoji={unknown} />);
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
    expect(screen.getAllByRole('img').map((image) => image.getAttribute('alt'))).toEqual(['🎉', '🇹🇷']);
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

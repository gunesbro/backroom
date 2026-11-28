import { fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { MenuButton } from '@backroom/ui';

// jsdom has no layout: the boxes are stubbed. The list is the scrolling ancestor, 0 to 400 px high.
const BOUNDS = { top: 0, bottom: 400 };

function box(top: number, bottom: number): DOMRect {
  return { top, bottom, height: bottom - top, left: 0, right: 0, width: 0, x: 0, y: top, toJSON: () => ({}) };
}

function stub(buttonTop: number, menuBottom: number) {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    if (this.dataset.scroller !== undefined) return box(BOUNDS.top, BOUNDS.bottom);
    if (this.classList.contains('ph-menu')) return box(menuBottom - 100, menuBottom);
    if (this.classList.contains('ph-menu-anchor')) return box(buttonTop, buttonTop + 28);
    return box(0, 0);
  });
}

function setup() {
  render(
    <div data-scroller style={{ overflowY: 'auto' }}>
      <MenuButton
        label="More"
        button="..."
        placement="auto"
        entries={[{ kind: 'item', id: 'a', label: 'A', onSelect: () => undefined }]}
      />
    </div>,
  );
  return screen.getByRole('button', { name: 'More' });
}

const panel = () => screen.getByRole('menu').closest('.ph-menu');

afterEach(() => {
  vi.restoreAllMocks();
});

describe('MenuButton placement auto', () => {
  it('Open_WhenItDoesNotFitBelowButFitsAbove_FlipsUpAndResetsAfterClose', () => {
    stub(350, 460);
    const button = setup();
    fireEvent.click(button);
    expect(panel()).toHaveClass('ph-menu--above');

    fireEvent.click(button);
    expect(screen.queryByRole('menu')).toBeNull();
    fireEvent.click(button);
    // Reopened: it starts below again and is measured again.
    expect(panel()).toHaveClass('ph-menu--above');
  });

  it('Open_WhenItFitsBelow_StaysBelow', () => {
    stub(100, 240);
    fireEvent.click(setup());
    expect(panel()).toHaveClass('ph-menu--below');
  });

  it('Open_WhenItFitsNeitherBelowNorAbove_StaysBelow', () => {
    stub(60, 460);
    fireEvent.click(setup());
    expect(panel()).toHaveClass('ph-menu--below');
  });
});

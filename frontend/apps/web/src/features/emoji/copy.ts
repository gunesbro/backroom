// Copying text that has emoji images in it must copy the emoji as characters, on every browser: as plain text and
// in the formatted copy too, never as images or addresses of images.

const EMOJI_IMAGE = 'img[data-emoji]';

/** The copied part of the page with each emoji image replaced by its character. */
function withCharacters(selection: Selection): HTMLDivElement | undefined {
  const holder = document.createElement('div');
  for (let index = 0; index < selection.rangeCount; index++) {
    holder.append(selection.getRangeAt(index).cloneContents());
  }
  const images = holder.querySelectorAll<HTMLImageElement>(EMOJI_IMAGE);
  if (images.length === 0) return undefined;
  for (const image of images) image.replaceWith(image.alt);
  return holder;
}

/** The text as the page lays it out (line breaks between blocks), which needs the copy to be in the page for a moment. */
function renderedText(holder: HTMLDivElement): string {
  holder.setAttribute('aria-hidden', 'true');
  holder.style.cssText = 'position:fixed;left:-10000px;top:0;white-space:pre-wrap;user-select:text';
  document.body.append(holder);
  try {
    // Without layout (tests) there is no rendered text.
    const rendered: unknown = holder.innerText;
    return typeof rendered === 'string' ? rendered : holder.textContent;
  } finally {
    holder.remove();
  }
}

/**
 * Handles a `copy` (or `cut`) event: when the selection has emoji images, the clipboard gets their characters.
 * Selections without emoji images, and copies from a text field or the editor, are left to the browser.
 */
export function copyEmojiAsText(event: ClipboardEvent): void {
  const selection = document.getSelection();
  if (!event.clipboardData || !selection || selection.isCollapsed) return;
  const target = event.target instanceof Element ? event.target : null;
  if (target?.closest('input, textarea, [contenteditable="true"]')) return;
  const holder = withCharacters(selection);
  if (!holder) return;
  const html = holder.innerHTML;
  event.clipboardData.setData('text/plain', renderedText(holder));
  event.clipboardData.setData('text/html', html);
  event.preventDefault();
}

/** Makes every copy on the page carry emoji as characters. Returns the function that stops it. */
export function installEmojiCopy(): () => void {
  document.addEventListener('copy', copyEmojiAsText);
  return () => {
    document.removeEventListener('copy', copyEmojiAsText);
  };
}

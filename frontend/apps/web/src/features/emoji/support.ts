// Some systems have no country flags in their emoji font: a flag shows as two letters there. The picker leaves
// the flags out where that is so.

const FONT = '24px "Segoe UI Emoji", "Apple Color Emoji", "Noto Color Emoji", sans-serif';
const SIZE = 32;

function detect(): boolean {
  // Nothing to draw on (tests, very old browsers): show them.
  if (typeof CanvasRenderingContext2D === 'undefined') return true;
  try {
    const canvas = document.createElement('canvas');
    canvas.width = SIZE * 2;
    canvas.height = SIZE;
    const context = canvas.getContext('2d', { willReadFrequently: true });
    if (!context) return true;
    context.textBaseline = 'top';
    context.font = FONT;
    context.fillStyle = '#000';
    // A real flag has colour; the two-letter fallback is drawn without any.
    context.fillText('\u{1F1FA}\u{1F1F3}', 0, 2);
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
    let coloured = 0;
    for (let at = 0; at < pixels.length; at += 4) {
      const red = pixels[at] ?? 0;
      const green = pixels[at + 1] ?? 0;
      const blue = pixels[at + 2] ?? 0;
      const alpha = pixels[at + 3] ?? 0;
      if (alpha > 0 && Math.max(red, green, blue) - Math.min(red, green, blue) > 40) coloured++;
    }
    return coloured >= 8;
  } catch {
    return true;
  }
}

let supported: boolean | undefined;

/** Whether this machine draws country flags as flags. Measured once. */
export function supportsFlagEmoji(): boolean {
  supported ??= detect();
  return supported;
}

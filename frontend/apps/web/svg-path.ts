// Path data of SVG images, written with fewer digits. Used when the emoji images are built (emoji-image-set.ts).

const COMMAND = /[a-zA-Z]/;
const NUMBER = /[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?/y;
const FLAG = /[01]/y;

/** A number with at most `digits` decimals, as short as SVG allows: "-0.50" is "-.5" and "2.04" is "2". */
function shortNumber(value: number, digits: number): string {
  const text = String(Number(value.toFixed(digits)));
  // Rounding can leave a negative zero.
  return text === '-0' ? '0' : text.replace(/^(-?)0\./, '$1.');
}

/**
 * The path data with its numbers rounded to `digits` decimals. The two flags of an arc are kept as they are (they may
 * be written without any separator, "a1 1 0 011 1"). Path data this does not understand makes it throw, so a build
 * fails instead of shipping a broken image.
 */
export function roundPathData(data: string, digits: number): string {
  let result = '';
  let command = '';
  let argument = 0;
  let needsSeparator = false;
  let at = 0;
  while (at < data.length) {
    const character = data.charAt(at);
    if (
      character === ' ' ||
      character === ',' ||
      character === '\n' ||
      character === '\t' ||
      character === '\r'
    ) {
      at += 1;
    } else if (COMMAND.test(character)) {
      command = character.toLowerCase();
      argument = 0;
      result += character;
      needsSeparator = false;
      at += 1;
    } else {
      // The fourth and fifth argument of every arc are flags: one character each.
      const pattern = command === 'a' && (argument % 7 === 3 || argument % 7 === 4) ? FLAG : NUMBER;
      pattern.lastIndex = at;
      const match = pattern.exec(data)?.[0];
      if (match === undefined || command === '' || command === 'z') {
        throw new Error(`Unexpected "${data.slice(at, at + 12)}" at ${String(at)} in path data`);
      }
      const text = pattern === FLAG ? match : shortNumber(Number(match), digits);
      result += needsSeparator && !text.startsWith('-') ? ` ${text}` : text;
      needsSeparator = true;
      argument += 1;
      at += match.length;
    }
  }
  return result;
}

/** The SVG text with the path data of every `d` attribute rounded to `digits` decimals. Nothing else changes. */
export function roundSvgPaths(svg: string, digits: number): string {
  return svg.replace(/(\sd=")([^"]*)"/g, (_, start: string, data: string) => {
    return `${start}${roundPathData(data, digits)}"`;
  });
}

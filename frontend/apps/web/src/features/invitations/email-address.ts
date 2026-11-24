// The client's copy of the server's email rule (`EmailAddress` in BuildingBlocks.Domain, docs/contracts/phase-1.md,
// "Cross-cutting rules"): exactly one address, an ASCII local part, trim, Unicode NFC, lower case that does not
// depend on the language (never toLocaleLowerCase: Turkish İ and ı), the domain converted to punycode. It only
// saves a round trip; the server decides, and its answer is what counts.

export const MAX_EMAIL_LENGTH = 254;
const MAX_LOCAL_PART = 64;
const MAX_DOMAIN = 253;
const MAX_LABEL = 63;
const LOCAL_SYMBOLS = "!#$%&'*+/=?^_`{|}~-";
/** Characters that would make the domain parse as something else than a host name (a path, port or user info). */
const NOT_HOST_CHARACTERS = [
  '/',
  String.fromCodePoint(92),
  '?',
  '#',
  ':',
  '@',
  '%',
  '[',
  ']',
  '<',
  '>',
  '^',
  '|',
];

function isControlOrSpace(code: number): boolean {
  return code <= 0x20 || code === 0x7f;
}

/**
 * .NET's ToLowerInvariant maps İ (U+0130) to a plain i and leaves ı (U+0131) alone, while toLowerCase turns İ into
 * i plus a combining dot (U+0307). Mapping İ first makes the two agree for the characters that can occur.
 */
function invariantLowerCase(text: string): string {
  return text.replaceAll(String.fromCodePoint(0x130), 'i').toLowerCase();
}

/**
 * The normalized address, or undefined when the server would refuse it. It is a check and a comparison key only
 * (the dialog sends the trimmed text and the server's normalization decides what is stored).
 * The normalized address, or undefined when the server would refuse it. */
export function normalizeEmail(input: string): string | undefined {
  if (input.length > MAX_EMAIL_LENGTH * 2) return undefined;
  let text: string;
  try {
    text = invariantLowerCase(input.trim().normalize('NFC'));
  } catch {
    return undefined;
  }

  const at = text.lastIndexOf('@');
  if (
    text.length > MAX_EMAIL_LENGTH ||
    at <= 0 ||
    at === text.length - 1 ||
    !isLocalPart(text.slice(0, at))
  ) {
    return undefined;
  }

  const domain = toAscii(text.slice(at + 1));
  if (domain === undefined || !isDomain(domain) || at + 1 + domain.length > MAX_EMAIL_LENGTH) {
    return undefined;
  }

  return `${text.slice(0, at + 1)}${domain}`;
}

function isLocalPart(local: string): boolean {
  if (local.length > MAX_LOCAL_PART || local.startsWith('.') || local.endsWith('.') || local.includes('..')) {
    return false;
  }

  for (const character of local) {
    if (!/^[A-Za-z0-9.]$/u.test(character) && !LOCAL_SYMBOLS.includes(character)) return false;
  }

  return true;
}

/** The host name in punycode (the URL parser applies IDNA), or undefined if it is not a plain host name. */
function toAscii(domain: string): string | undefined {
  if (
    domain === '' ||
    NOT_HOST_CHARACTERS.some((character) => domain.includes(character)) ||
    Array.from(domain, (character) => character.codePointAt(0) ?? 0).some(isControlOrSpace)
  )
    return undefined;
  // A plain ASCII name needs no IDNA (and the URL parser would read a last label such as 0x10 as an IPv4 number).
  if (Array.from(domain, (character) => character.codePointAt(0) ?? 0).every((code) => code < 0x80)) {
    return domain;
  }

  try {
    return new URL(`http://${domain}`).hostname.toLowerCase();
  } catch {
    return undefined;
  }
}

const PUNYCODE_BASE = 36;
const PUNYCODE_T_MIN = 1;
const PUNYCODE_T_MAX = 26;

function punycodeAdapt(delta: number, count: number, first: boolean): number {
  let value = Math.floor(first ? delta / 700 : delta / 2);
  value += Math.floor(value / count);
  let k = 0;
  while (value > ((PUNYCODE_BASE - PUNYCODE_T_MIN) * PUNYCODE_T_MAX) / 2) {
    value = Math.floor(value / (PUNYCODE_BASE - PUNYCODE_T_MIN));
    k += PUNYCODE_BASE;
  }

  return k + Math.floor(((PUNYCODE_BASE - PUNYCODE_T_MIN + 1) * value) / (value + 38));
}

/** RFC 3492 decoding of the part after "xn--", or undefined if it is not valid punycode. */
function decodePunycode(input: string): string | undefined {
  const output: number[] = [];
  const delimiter = input.lastIndexOf('-');
  for (const character of delimiter > 0 ? input.slice(0, delimiter) : '') {
    output.push(character.codePointAt(0) ?? 0);
  }

  let n = 128;
  let i = 0;
  let bias = 72;
  let index = delimiter > 0 ? delimiter + 1 : 0;
  while (index < input.length) {
    const previous = i;
    let weight = 1;
    for (let k = PUNYCODE_BASE; ; k += PUNYCODE_BASE) {
      const character = input.charCodeAt(index++);
      if (Number.isNaN(character)) return undefined;
      const digit =
        character >= 97 && character <= 122
          ? character - 97
          : character >= 48 && character <= 57
            ? character - 22
            : -1;
      if (digit < 0) return undefined;
      i += digit * weight;
      const threshold = k <= bias ? PUNYCODE_T_MIN : Math.min(k - bias, PUNYCODE_T_MAX);
      if (digit < threshold) break;
      weight *= PUNYCODE_BASE - threshold;
      if (i > 0x7fffffff || weight > 0x7fffffff) return undefined;
    }

    const count = output.length + 1;
    bias = punycodeAdapt(i - previous, count, previous === 0);
    n += Math.floor(i / count);
    if (n > 0x10ffff) return undefined;
    i %= count;
    output.splice(i, 0, n);
    i += 1;
  }

  try {
    return String.fromCodePoint(...output);
  } catch {
    return undefined;
  }
}

/** An "xn--" label the server's IDNA would accept: it decodes, has non-ASCII text, and encodes back to itself. */
function isValidAceLabel(label: string): boolean {
  if (!label.startsWith('xn--')) return true;
  const decoded = decodePunycode(label.slice(4));
  if (
    decoded === undefined ||
    Array.from(decoded).every((character) => (character.codePointAt(0) ?? 0) < 0x80)
  ) {
    return false;
  }

  try {
    return new URL(`http://${decoded}`).hostname === label;
  } catch {
    return false;
  }
}

// At least two labels, none empty or starting or ending with '-', the last one not a number.
function isDomain(domain: string): boolean {
  if (domain.length > MAX_DOMAIN || !domain.includes('.')) return false;
  const labels = domain.split('.');
  for (const label of labels) {
    if (label.length === 0 || label.length > MAX_LABEL || label.startsWith('-') || label.endsWith('-')) {
      return false;
    }

    if (!/^[a-z0-9-]+$/u.test(label) || !isValidAceLabel(label)) return false;
  }

  return !/^[0-9]+$/u.test(labels.at(-1) ?? '');
}

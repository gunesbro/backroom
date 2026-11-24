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

/** The normalized address, or undefined when the server would refuse it. */
export function normalizeEmail(input: string): string | undefined {
  if (input.length > MAX_EMAIL_LENGTH * 2) return undefined;
  let text: string;
  try {
    text = input.trim().normalize('NFC').toLowerCase();
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
  try {
    return new URL(`http://${domain}`).hostname.toLowerCase();
  } catch {
    return undefined;
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

    if (!/^[a-z0-9-]+$/u.test(label)) return false;
  }

  return !/^[0-9]+$/u.test(labels.at(-1) ?? '');
}

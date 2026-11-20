/** A code is base64url of 256 bits (docs/contracts/phase-1.md, "Code"): 43 characters without padding. */
const CODE = /^[A-Za-z0-9_-]{43}$/;

export function isInvitationCode(text: string): boolean {
  return CODE.test(text);
}

/**
 * The invitation code from a pasted link (`https://<web app>/invite#<code>`) or a bare code; undefined when the
 * text is neither. Only the fragment is read, so the code is never part of a URL sent anywhere.
 */
export function extractInvitationCode(text: string): string | undefined {
  const trimmed = text.trim();
  if (CODE.test(trimmed)) {
    return trimmed;
  }

  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return undefined;
  }

  const code = url.hash.slice(1);
  return url.pathname === '/invite' && CODE.test(code) ? code : undefined;
}

import { describe, expect, it } from 'vitest';
import { extractInvitationCode } from './invitation-code';

const code = 'abcDEF0123456789_-abcDEF0123456789_-abcDEF0';

describe('extractInvitationCode', () => {
  it.each([
    [code, code],
    [`  ${code}\n`, code],
    [`https://app.example.test/invite#${code}`, code],
  ])('Extract_FromValidText_ReturnsTheCode (%s)', (text, expected) => {
    expect(extractInvitationCode(text)).toBe(expected);
  });

  it.each([
    '',
    code.slice(1),
    `${code}A`,
    `${code.slice(1)}=`,
    `https://app.example.test/other#${code}`,
    `https://app.example.test/invite?code=${code}`,
    `https://app.example.test/invite#${code}x`,
    'not a link',
  ])('Extract_FromInvalidText_ReturnsUndefined (%s)', (text) => {
    expect(extractInvitationCode(text)).toBeUndefined();
  });
});

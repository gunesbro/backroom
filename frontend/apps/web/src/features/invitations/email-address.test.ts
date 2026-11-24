import { describe, expect, it } from 'vitest';
import { normalizeEmail } from './email-address';

describe('normalizeEmail', () => {
  it.each([
    ['Deniz@Acme.COM', 'deniz@acme.com'],
    ['  deniz@acme.com  ', 'deniz@acme.com'],
    ["o'brien+tag@acme.co.uk", "o'brien+tag@acme.co.uk"],
    ['ali@bücher.example', 'ali@xn--bcher-kva.example'],
    ['a@istanbul.example', 'a@istanbul.example'],
  ])('Normalize_ValidAddress_%s_ReturnsTheServersForm', (input, expected) => {
    expect(normalizeEmail(input)).toBe(expected);
  });

  it('Normalize_UpperCaseDottedAndDotlessI_FoldsWithoutTheLanguageOfTheMachine', () => {
    // ISTANBUL has an ASCII I: it becomes i, never the Turkish dotless ı.
    expect(normalizeEmail('INFO@ISTANBUL.EXAMPLE')).toBe('info@istanbul.example');
    expect(normalizeEmail('INFO@ISTANBUL.EXAMPLE')).not.toContain('ı');
  });

  it.each(['ılker@acme.com', 'ıli@ıstanbul.example'])('Normalize_NonAsciiLocalPart_%s_IsRefused', (input) => {
    expect(normalizeEmail(input)).toBeUndefined();
  });

  it('Normalize_TurkishCapitalIInLocalPart_FoldsToAsciiIBeforeTheAsciiCheck', () => {
    expect(normalizeEmail('İlker@acme.com')).toBe('ilker@acme.com');
  });

  it('Normalize_TurkishCapitalIInDomain_FoldsToAsciiIAsTheServerDoes', () => {
    // .NET's ToLowerInvariant maps İ to a plain i; the client must not leave a combining dot behind.
    expect(normalizeEmail('a@İSTANBUL.example')).toBe('a@istanbul.example');
    expect(normalizeEmail('a@ISTANBUL.EXAMPLE')).toBe('a@istanbul.example');
  });

  it('Normalize_DotlessIInDomain_StaysDotlessAndIsConvertedToPunycode', () => {
    expect(normalizeEmail('a@ıstanbul.example')).toBe('a@xn--stanbul-qfb.example');
  });

  it('Normalize_ServerParity_InvalidPunycodeIsRefusedAndAHexLikeLastLabelIsAccepted', () => {
    expect(normalizeEmail('x@xn--abc.com')).toBeUndefined();
    expect(normalizeEmail('x@foo.0x10')).toBe('x@foo.0x10');
    expect(normalizeEmail('x@xn--bcher-kva.example')).toBe('x@xn--bcher-kva.example');
  });

  it.each([
    '',
    'deniz',
    '@acme.com',
    'deniz@',
    'deniz@acme',
    'deniz@1.2.3.4',
    'deniz@acme.123',
    'deniz@@acme.com',
    'a b@acme.com',
    'a,b@acme.com',
    'Deniz <deniz@acme.com>',
    '"deniz"@acme.com',
    'deniz\r\n@acme.com',
    'deniz@acme.com\r\nbcc:x@acme.com',
    '.deniz@acme.com',
    'deniz.@acme.com',
    'de..niz@acme.com',
    'deniz@-acme.com',
    'deniz@acme-.com',
    'deniz@acme..com',
    'deniz@acme.com.',
    'deniz@acme.com/path',
    'deniz@acme.com:8080',
    'deniz@[1.2.3.4]',
    'ｄeniz@acme.com',
    'a\uD800@acme.com',
  ])('Normalize_InvalidAddress_%j_IsRefused', (input) => {
    expect(normalizeEmail(input)).toBeUndefined();
  });

  it('Normalize_Limits_FollowTheServer', () => {
    expect(normalizeEmail(`${'a'.repeat(64)}@acme.com`)).toBeDefined();
    expect(normalizeEmail(`${'a'.repeat(65)}@acme.com`)).toBeUndefined();
    expect(normalizeEmail(`a@${'b'.repeat(63)}.com`)).toBeDefined();
    expect(normalizeEmail(`a@${'b'.repeat(64)}.com`)).toBeUndefined();
    const long = `a@${['b'.repeat(63), 'c'.repeat(63), 'd'.repeat(63), 'e'.repeat(62)].join('.')}`;
    expect(long.length).toBeGreaterThan(254);
    expect(normalizeEmail(long)).toBeUndefined();
  });
});

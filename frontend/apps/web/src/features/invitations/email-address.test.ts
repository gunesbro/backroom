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

  it.each(['İlker@acme.com', 'ılker@acme.com', 'ali@ıstanbul.example'.replace('ali', 'ıli')])(
    'Normalize_NonAsciiLocalPart_%s_IsRefused',
    (input) => {
      expect(normalizeEmail(input)).toBeUndefined();
    },
  );

  it('Normalize_TurkishCapitalIInDomain_IsConvertedToPunycodeNotToAsciiI', () => {
    const result = normalizeEmail('a@İSTANBUL.example');
    expect(result).toBeDefined();
    expect(result).not.toBe('a@istanbul.example');
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

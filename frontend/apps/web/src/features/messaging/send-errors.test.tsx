import { describe, expect, it } from 'vitest';
import { isRefusedContent, refusalKey } from './send-errors';

describe('refusalKey', () => {
  it.each([
    ['messaging.message_invalid', 'links.limit', 'errorLinksLimit'],
    ['messaging.message_invalid', 'link.href', 'errorLinkHref'],
    ['messaging.message_invalid', 'blocks.size', 'formattingTooLarge'],
    ['messaging.message_invalid', 'blocks.disabled', 'errorBlocksDisabled'],
    ['messaging.message_invalid', 'text.length', 'errorTextLength'],
    ['messaging.message_invalid', 'paragraph.blank_edge', 'errorEmpty'],
    ['messaging.message_invalid', 'quote.empty', 'errorEmpty'],
    ['messaging.message_invalid', 'blocks.encoding', 'errorCharacters'],
    ['messaging.message_invalid', 'something.new', 'messageInvalid'],
    ['messaging.message_invalid', undefined, 'messageInvalid'],
    ['messaging.mention_invalid', 'group_mentions_unavailable', 'errorGroupMention'],
    ['messaging.mention_invalid', 'mention.account_id', 'errorMention'],
    ['messaging.mention_limit', undefined, 'errorMentionLimit'],
  ])('RefusalKey_When%s_%s_Returns%s', (code, reason, expected) => {
    expect(refusalKey(code, reason)).toBe(expected);
  });

  it('IsRefusedContent_WhenTransportError_IsFalse', () => {
    expect(isRefusedContent('messaging.message_invalid')).toBe(true);
    expect(isRefusedContent('http.502')).toBe(false);
    expect(isRefusedContent(undefined)).toBe(false);
  });
});

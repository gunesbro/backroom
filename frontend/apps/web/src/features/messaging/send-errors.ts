import type { MessageKey } from './messages';

/** The error codes a refused message can carry (the cell answers 400 with one of these and a fixed `reason`). */
const REFUSED_CODES: ReadonlySet<string> = new Set([
  'messaging.message_invalid',
  'messaging.mention_invalid',
  'messaging.mention_limit',
  'messaging.reply_invalid',
  'messaging.message_reply_unavailable',
]);

/** The server refused the content itself: sending it again unchanged cannot work. */
export function isRefusedContent(code: string | undefined): boolean {
  return code !== undefined && REFUSED_CODES.has(code);
}

/**
 * The text that explains a refused send (docs/contracts/phase-2.md, "Block schema (exact)": `reason` is a fixed token
 * naming the place). A reason that is not known gets the general text of its code, never the token itself.
 */
export function refusalKey(code: string, reason: string | undefined): MessageKey {
  if (code === 'messaging.mention_limit') return 'errorMentionLimit';
  if (code === 'messaging.message_reply_unavailable') return 'replyUnavailable';
  if (code === 'messaging.reply_invalid') {
    return reason === 'reply.deleted' || reason === 'reply.not_found' ? 'replyGone' : 'messageInvalid';
  }
  if (code === 'messaging.mention_invalid') {
    return reason === 'group_mentions_unavailable' ? 'errorGroupMention' : 'errorMention';
  }
  switch (reason) {
    case 'links.limit':
      return 'errorLinksLimit';
    case 'link.href':
      return 'errorLinkHref';
    case 'blocks.size':
      return 'formattingTooLarge';
    case 'blocks.disabled':
      return 'errorBlocksDisabled';
    case 'text.length':
      return 'errorTextLength';
    case 'blocks.empty':
    case 'text.empty':
    case 'text':
    case 'paragraph.blank_edge':
    case 'quote.empty':
    case 'code_block.empty':
    case 'list_item.empty':
    case 'list.children':
      return 'errorEmpty';
    case 'blocks.encoding':
    case 'text.text':
      return 'errorCharacters';
    default:
      return 'messageInvalid';
  }
}

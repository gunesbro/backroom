/** The message the next one in a conversation replies to, kept per conversation while the person writes. */
export type ReplyTarget = {
  messageId: string;
  authorAccountId: string;
  /** The original's text, cut as the server cuts a quote (`truncateQuote`). */
  text: string;
};

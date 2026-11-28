/** The message the next one in a conversation replies to, kept per conversation while the person writes. */
export type ReplyTarget = {
  /** The text is read from the loaded messages when it is shown, so an edit or a delete reaches the bar. */
  messageId: string;
  /**
   * The original's author, for the bar while the original is not in the view (a jump's window in the history).
   * Missing in targets made before it was kept.
   */
  authorAccountId?: string;
};

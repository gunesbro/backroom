/** The message the next one in a conversation replies to, kept per conversation while the person writes. */
export type ReplyTarget = {
  /** Only the id: the author and the text are read from the loaded messages when they are shown. */
  messageId: string;
};

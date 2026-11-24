/**
 * The key of a row, stable from the unsent message to the confirmed one. The client message id is chosen by the
 * sender, so it is qualified by the author: two senders can pick the same one.
 */
export function rowKey(message: { authorAccountId: string; clientMessageId: string; id: string }): string {
  return message.clientMessageId ? `${message.authorAccountId}:${message.clientMessageId}` : message.id;
}

/** Cache key of an organization's pending invitations; mutations invalidate it. */
export const invitationsKey = (orgId: string) => ['invitations', orgId] as const;

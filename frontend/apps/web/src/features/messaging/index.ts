// Public surface of the messaging feature: channels and messages of an organization, served by its cell.
import { lazy } from 'react';

export {
  createMessagingApi,
  MessagingApiContext,
  useMessagingEnabled,
  type Cell,
  type CellTokenSource,
  type MessagingApi,
  type MessagingApiFactory,
} from './api';

/** The organization's workspace, loaded only when an organization is opened. */
export const Workspace = lazy(() =>
  import('./workspace').then(({ Workspace: workspace }) => ({ default: workspace })),
);

// Public surface of the presence feature: the own status (control plane), the status menu and activity reporting.
export {
  createStatusApi,
  listPresence,
  StatusApiContext,
  usePresenceEnabled,
  type AccountStatus,
  type MemberPresence,
  type StatusApi,
  type StatusInput,
} from './api';
export { PresenceDot, type OwnPresence } from './presence-dot';
export { useStatusMenu, type StatusMenu } from './use-status-menu';
// Not used by any view yet: the web shows nobody else's presence so far. A view that does (member lists, DMs) must
// read each entry through `currentPresence` and pass `presenceExpiries` to `useExpiryClock`, because the server
// sends no event when someone's do not disturb or custom status ends. Today the clock serves the own status only.
export { useExpiryClock } from './use-expiry-clock';
export { currentPresence, presenceExpiries } from './status';
export { useActivityReporting, useLocalActivity } from './use-activity';

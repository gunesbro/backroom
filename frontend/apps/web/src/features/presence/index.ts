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
export { useExpiryClock } from './use-expiry-clock';
export { currentPresence, presenceExpiries } from './status';
export { useActivityReporting, useLocalActivity } from './use-activity';

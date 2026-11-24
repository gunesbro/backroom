import type { Messages } from '@backroom/i18n';

export type MessageKey =
  | 'title'
  | 'caption'
  | 'type'
  | 'region'
  | 'version'
  | 'link'
  | 'heartbeat'
  | 'organizations'
  | 'pending'
  | 'rejected'
  | 'connected'
  | 'disconnected'
  | 'awaitingRegistration'
  | 'never'
  | 'refresh'
  | 'refreshing'
  | 'autoRefresh'
  | 'loading'
  | 'empty'
  | 'error'
  | 'retry'
  | 'forbidden'
  | 'unknownVersion';

export const messages: Messages<MessageKey> = {
  en: {
    title: 'Cells',
    caption: 'Cells and their link status',
    type: 'Type',
    region: 'Region',
    version: 'Version',
    link: 'Link',
    heartbeat: 'Last heartbeat',
    organizations: 'Organizations',
    pending: 'Pending messages',
    rejected: 'Rejected messages',
    connected: 'Connected',
    disconnected: 'Disconnected',
    awaitingRegistration: 'Awaiting registration',
    never: 'Never',
    refresh: 'Refresh',
    refreshing: 'Refreshing…',
    autoRefresh: 'Refresh every 30 seconds',
    loading: 'Loading cells…',
    empty: 'No cells yet.',
    error: 'The cells could not be loaded.',
    retry: 'Try again',
    forbidden: 'Ops role and two-step verification required',
    unknownVersion: 'Unknown',
  },
  tr: {
    title: 'Hücreler',
    caption: 'Hücreler ve bağlantı durumları',
    type: 'Tür',
    region: 'Bölge',
    version: 'Sürüm',
    link: 'Bağlantı',
    heartbeat: 'Son nabız',
    organizations: 'Kuruluşlar',
    pending: 'Bekleyen iletiler',
    rejected: 'Reddedilen iletiler',
    connected: 'Bağlı',
    disconnected: 'Bağlı değil',
    awaitingRegistration: 'Kayıt bekleniyor',
    never: 'Hiç',
    refresh: 'Yenile',
    refreshing: 'Yenileniyor…',
    autoRefresh: '30 saniyede bir yenile',
    loading: 'Hücreler yükleniyor…',
    empty: 'Henüz hücre yok.',
    error: 'Hücreler yüklenemedi.',
    retry: 'Tekrar dene',
    forbidden: 'Ops rolü ve iki adımlı doğrulama gerekli',
    unknownVersion: 'Bilinmiyor',
  },
};

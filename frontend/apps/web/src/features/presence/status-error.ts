import { ApiError } from '@/shared/http';

type Translate = (key: 'rateLimited' | 'invalid' | 'unavailable' | 'generic') => string;

/** The message for a refused change; the server's code decides (rate limited, invalid, presence off). */
export function statusErrorText(error: unknown, t: Translate): string {
  if (error instanceof ApiError) {
    if (error.code === 'identity.rate_limited' || error.status === 429) return t('rateLimited');
    if (error.code === 'identity.status_invalid' || error.code === 'identity.time_zone_invalid')
      return t('invalid');
    if (error.status === 404) return t('unavailable');
  }
  return t('generic');
}

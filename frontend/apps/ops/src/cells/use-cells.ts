import { useEffect, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { ApiError, type AccessTokenSource } from '@/shared/http';
import { cellsQueryKey, fetchCells } from './cell-api';

/** The automatic refresh interval. */
export const AUTO_REFRESH_MS = 30_000;

function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState(() => document.visibilityState === 'visible');
  useEffect(() => {
    const update = () => {
      setVisible(document.visibilityState === 'visible');
    };
    document.addEventListener('visibilitychange', update);
    return () => {
      document.removeEventListener('visibilitychange', update);
    };
  }, []);
  return visible;
}

/**
 * The cell list. With `autoRefresh` it reloads every 30 seconds, but only while the tab is visible: a hidden
 * tab does no background polling.
 */
export function useCells(getAccessToken: AccessTokenSource, autoRefresh: boolean) {
  const visible = useDocumentVisible();
  return useQuery({
    queryKey: cellsQueryKey,
    queryFn: ({ signal }) => fetchCells(getAccessToken, signal),
    refetchInterval: autoRefresh && visible ? AUTO_REFRESH_MS : false,
    refetchIntervalInBackground: false,
    refetchOnWindowFocus: false,
    // Authorization problems do not fix themselves; other failures get one more try.
    retry: (count, error) => !(error instanceof ApiError && [401, 403].includes(error.status)) && count < 1,
  });
}

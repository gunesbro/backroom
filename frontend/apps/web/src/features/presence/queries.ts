import { useContext } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { ApiError } from '@/shared/http';
import { StatusApiContext, type AccountStatus, type StatusInput } from './api';
import { FALLBACK_TIME_ZONE } from './status';

/** The signed-in person's own status (control plane). */
export const MY_STATUS = ['me', 'status'] as const;

/** Not asked for while presence is off. */
export function useMyStatus() {
  const api = useContext(StatusApiContext);
  return useQuery({
    queryKey: MY_STATUS,
    queryFn: ({ signal }) => {
      if (!api) throw new Error('Presence is off.');
      return api.get(signal);
    },
    enabled: api !== null,
  });
}

/** Replaces the status; shown at once, put back if the server refuses, and taken from the answer when it accepts. */
export function useSetStatus() {
  const api = useContext(StatusApiContext);
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (input: StatusInput) => {
      if (!api) throw new Error('Presence is off.');
      try {
        return await api.put(input);
      } catch (error) {
        // A zone the server does not know (an older tz database): the status is still set, with UTC as the zone.
        if (
          error instanceof ApiError &&
          error.code === 'identity.time_zone_invalid' &&
          input.timeZone !== FALLBACK_TIME_ZONE
        ) {
          return api.put({ ...input, timeZone: FALLBACK_TIME_ZONE });
        }
        throw error;
      }
    },
    onMutate: async (input) => {
      await queryClient.cancelQueries({ queryKey: MY_STATUS });
      const previous = queryClient.getQueryData<AccountStatus>(MY_STATUS);
      queryClient.setQueryData<AccountStatus>(MY_STATUS, {
        dndUntil: input.dndUntil,
        customStatus: input.customStatus,
        timeZone: input.timeZone,
        version: previous?.version ?? 0,
      });
      return { previous };
    },
    onError: (_error, _input, context) => {
      queryClient.setQueryData(MY_STATUS, context?.previous);
    },
    onSuccess: (status) => {
      queryClient.setQueryData(MY_STATUS, status);
    },
  });
}

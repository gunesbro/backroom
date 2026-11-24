import { useState } from 'react';
import { formatDateTime, type Locale } from '@backroom/i18n';
import { Alert, Button, Heading, Spinner, Status } from '@backroom/ui';
import { ApiError, type AccessTokenSource } from '@/shared/http';
import { formatRelative, translator } from '@/shared/locale';
import type { Cell } from './cell-api';
import { messages, type MessageKey } from './messages';
import { useCells } from './use-cells';
import './cell-list.css';

type Translate = (key: MessageKey) => string;

function LinkStatus({ cell, t }: { cell: Cell; t: Translate }) {
  if (cell.status === 'pending') {
    return <span className="ops-link ops-link--pending">{t('awaitingRegistration')}</span>;
  }

  const connected = cell.linkStatus === 'connected';
  // The text carries the meaning; the symbol only repeats it, so color is never the only signal.
  return (
    <span className={`ops-link ${connected ? 'ops-link--up' : 'ops-link--down'}`}>
      <span aria-hidden="true">{connected ? '●' : '○'}</span> {connected ? t('connected') : t('disconnected')}
    </span>
  );
}

function Heartbeat({ cell, locale, t }: { cell: Cell; locale: Locale; t: Translate }) {
  if (cell.lastHeartbeatAt === null) return <>{t('never')}</>;
  const at = new Date(cell.lastHeartbeatAt);
  return (
    <time dateTime={cell.lastHeartbeatAt}>
      {formatRelative(at, new Date(), locale)}
      <small className="ops-absolute">{formatDateTime(at, locale)}</small>
    </time>
  );
}

export function CellList({ getAccessToken, locale }: { getAccessToken: AccessTokenSource; locale: Locale }) {
  const t = translator(messages, locale);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const cells = useCells(getAccessToken, autoRefresh);
  const forbidden = cells.error instanceof ApiError && cells.error.status === 403;

  return (
    <section aria-labelledby="cells-title" className="ops-cells">
      <div className="ops-cells__bar">
        <Heading id="cells-title" level={2}>
          {t('title')}
        </Heading>
        <label>
          <input
            type="checkbox"
            checked={autoRefresh}
            onChange={(event) => {
              setAutoRefresh(event.target.checked);
            }}
          />{' '}
          {t('autoRefresh')}
        </label>
        <Button
          disabled={cells.isFetching}
          onClick={() => {
            void cells.refetch();
          }}
        >
          {cells.isFetching && !cells.isPending ? t('refreshing') : t('refresh')}
        </Button>
      </div>

      {cells.isPending && (
        <Status>
          <Spinner /> {t('loading')}
        </Status>
      )}

      {cells.isError && (
        <Alert>
          {forbidden ? (
            t('forbidden')
          ) : (
            <>
              {t('error')}{' '}
              <Button
                onClick={() => {
                  void cells.refetch();
                }}
              >
                {t('retry')}
              </Button>
            </>
          )}
        </Alert>
      )}

      {cells.isSuccess && cells.data.length === 0 && <p>{t('empty')}</p>}

      {cells.isSuccess && cells.data.length > 0 && (
        <div className="ops-cells__scroll">
          <table className="ops-table">
            <caption className="ops-sr-only">{t('caption')}</caption>
            <thead>
              <tr>
                <th scope="col">ID</th>
                <th scope="col">{t('type')}</th>
                <th scope="col">{t('region')}</th>
                <th scope="col">{t('version')}</th>
                <th scope="col">{t('link')}</th>
                <th scope="col">{t('heartbeat')}</th>
                <th scope="col" className="ops-num">
                  {t('organizations')}
                </th>
                <th scope="col" className="ops-num">
                  {t('pending')}
                </th>
                <th scope="col" className="ops-num">
                  {t('rejected')}
                </th>
              </tr>
            </thead>
            <tbody>
              {cells.data.map((cell) => (
                <tr key={cell.id}>
                  <th scope="row">{cell.id}</th>
                  <td>{cell.type}</td>
                  <td>{cell.region}</td>
                  <td>{cell.version ?? t('unknownVersion')}</td>
                  <td>
                    <LinkStatus cell={cell} t={t} />
                  </td>
                  <td>
                    <Heartbeat cell={cell} locale={locale} t={t} />
                  </td>
                  <td className="ops-num">{cell.organizationCount}</td>
                  <td className="ops-num">{cell.pendingMessages}</td>
                  <td className="ops-num">{cell.rejectedMessages}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

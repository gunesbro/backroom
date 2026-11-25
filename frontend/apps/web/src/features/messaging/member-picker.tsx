import { useEffect, useId, useMemo, useState } from 'react';
import { Alert, Avatar, Icon, Spinner, Status } from '@backroom/ui';
import { useLocale, useTranslate } from '@/shared/locale';
import { MAX_ADDED_MEMBERS, type MessagingApi } from './api';
import { messages } from './messages';
import { MAX_MEMBER_PAGES, useOrganizationMembers } from './queries';

export type PickedMember = { accountId: string; displayName: string };

/** Rows drawn at most; a longer match is narrowed by typing more of the name. */
const MAX_SHOWN = 50;
/** Members the picker can search: the pages the workspace holds, 100 each. */
const SEARCHED_MEMBERS = MAX_MEMBER_PAGES * 100;

/**
 * docs/design: Dialogs, "Kişi ekle": the organization's active members to choose from, by name, up to 50. The
 * people chosen are chips above the search field; the matches are checkboxes below it. The member list has no
 * search of its own yet, so the pages held (500 members at most) are searched here, and email addresses are not
 * searched at all: the cell shows nobody another person's address.
 */
export function MemberPicker({
  api,
  orgId,
  label,
  selected,
  onChange,
  excluded,
  disabled = false,
}: {
  api: MessagingApi;
  orgId: string;
  label: string;
  selected: readonly PickedMember[];
  onChange: (selected: readonly PickedMember[]) => void;
  /** Account ids that cannot be chosen: the person themself, and people already in the channel. */
  excluded: ReadonlySet<string>;
  disabled?: boolean;
}) {
  const t = useTranslate(messages);
  const locale = useLocale();
  const hintId = useId();
  const [search, setSearch] = useState('');
  const members = useOrganizationMembers(api, orgId);

  // Every page the workspace may hold, so the search covers as much as it can.
  const { hasNextPage, isFetchingNextPage, fetchNextPage, data } = members;
  const pages = data?.pages.length ?? 0;
  useEffect(() => {
    if (hasNextPage && !isFetchingNextPage && pages > 0 && pages < MAX_MEMBER_PAGES) void fetchNextPage();
  }, [hasNextPage, isFetchingNextPage, pages, fetchNextPage]);

  const candidates = useMemo(
    () =>
      (data?.pages ?? [])
        .flatMap((page) => page.members)
        // The person's own entry is the only one with an email address.
        .filter((member) => member.email === null && !excluded.has(member.accountId)),
    [data, excluded],
  );
  const matches = useMemo(() => {
    const needle = search.trim().toLocaleLowerCase(locale);
    return needle === ''
      ? candidates
      : candidates.filter((member) => member.displayName.toLocaleLowerCase(locale).includes(needle));
  }, [candidates, search, locale]);
  const shown = matches.slice(0, MAX_SHOWN);
  const chosen = useMemo(() => new Set(selected.map((member) => member.accountId)), [selected]);
  const full = selected.length >= MAX_ADDED_MEMBERS;

  const toggle = (member: PickedMember) => {
    onChange(
      chosen.has(member.accountId)
        ? selected.filter((other) => other.accountId !== member.accountId)
        : [...selected, { accountId: member.accountId, displayName: member.displayName }],
    );
  };

  return (
    <fieldset className="bm-picker" disabled={disabled}>
      <legend className="ph-field__label">{label}</legend>
      {selected.length > 0 && (
        <ul className="bm-chips" aria-label={t('chosenPeople')}>
          {selected.map((member) => (
            <li key={member.accountId} className="bm-chip">
              <Avatar name={member.displayName} toneKey={member.accountId} size="sm" />
              <span className="bm-chip__name">{member.displayName}</span>
              <button
                type="button"
                className="bm-chip__remove"
                aria-label={t('removePerson', { name: member.displayName })}
                onClick={() => {
                  toggle(member);
                }}
              >
                <Icon name="close" size={12} />
              </button>
            </li>
          ))}
        </ul>
      )}
      <input
        type="search"
        className="ph-input"
        value={search}
        maxLength={200}
        autoComplete="off"
        placeholder={t('searchPeople')}
        aria-label={t('searchPeople')}
        aria-describedby={hintId}
        onChange={(event) => {
          setSearch(event.target.value);
        }}
      />
      {members.isPending && (
        <Status>
          <Spinner />
          {t('loading')}
        </Status>
      )}
      {members.isError && !data && <Alert>{t('generic')}</Alert>}
      {data && shown.length === 0 && <p className="ph-muted">{t('noPeopleFound')}</p>}
      <ul className="bm-picker__list">
        {shown.map((member) => {
          const checked = chosen.has(member.accountId);
          return (
            <li key={member.accountId}>
              <label className="bm-picker__row">
                <input
                  type="checkbox"
                  checked={checked}
                  disabled={!checked && full}
                  onChange={() => {
                    toggle(member);
                  }}
                />
                <Avatar name={member.displayName} toneKey={member.accountId} size="sm" />
                <span className="bm-picker__name">{member.displayName}</span>
              </label>
            </li>
          );
        })}
      </ul>
      <p id={hintId} className="ph-field__hint bm-picker__hint">
        {full
          ? t('peopleLimit', { count: MAX_ADDED_MEMBERS })
          : matches.length > MAX_SHOWN
            ? t('morePeopleMatch', { count: MAX_SHOWN })
            : hasNextPage && pages >= MAX_MEMBER_PAGES
              ? t('peoplePartial', { count: SEARCHED_MEMBERS })
              : ''}
      </p>
    </fieldset>
  );
}

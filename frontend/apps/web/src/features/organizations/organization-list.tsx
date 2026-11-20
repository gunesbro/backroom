import { useId } from 'react';
import { Card, ChoiceList, Heading, LinkButton } from '@backroom/ui';
import { useTranslate } from '@/shared/locale';
import type { AccountOrganization } from './api';
import { messages } from './messages';
import { SignedInPage } from './signed-in-page';

type Props = {
  organizations: readonly AccountOrganization[];
  onChoose: (id: string) => void;
  onCreate: () => void;
};

export function OrganizationList({ organizations, onChoose, onCreate }: Props) {
  const t = useTranslate(messages);
  const headingId = useId();
  return (
    <SignedInPage>
      <Card narrow labelledBy={headingId}>
        <Heading id={headingId}>{t('yourOrganizations')}</Heading>
        <ChoiceList items={organizations.map(({ id, name }) => ({ id, label: name }))} onChoose={onChoose} />
        <LinkButton onClick={onCreate}>{t('createLink')}</LinkButton>
      </Card>
    </SignedInPage>
  );
}

# Deviations from the design

Where the web app knowingly differs from the design sources in `docs/design/source`, and why. Remove an entry when the
difference is gone.

## Texts not taken verbatim

| Where                                  | Design                                      | App                       | Why                                                                     |
| -------------------------------------- | ------------------------------------------- | ------------------------- | ----------------------------------------------------------------------- |
| Invite dialog title, organization menu | "Acme'ye davet et", "Acme'ye kişi davet et" | "Acme için kişi davet et" | A Turkish suffix cannot be put right on every organization name.        |
| Invite dialog tab list name            | none                                        | "Davet yolu"              | The tab list needs an accessible name.                                  |
| Account bar region                     | none                                        | "Hesap"                   | Everything on the page must sit in a landmark; the region needs a name. |
| Controls of later phases               | none                                        | "Yakında"                 | Phase 2 and later controls are shown disabled and say they are coming.  |

## Structure

| Where                 | Design                           | App               | Why                                      |
| --------------------- | -------------------------------- | ----------------- | ---------------------------------------- |
| Organization switcher | "128 üye" under the name         | name only         | Member counts are not available yet.     |
| Account bar           | "Çevrimiçi · Odamda"             | the email address | Presence and rooms come in later phases. |
| Sidebar shortcuts     | "Kat 2 · 6 kişi", "14:00 Sprint" | "Yakında"         | Rooms and calendar come in later phases. |

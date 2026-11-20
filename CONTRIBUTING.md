# Contributing to Backroom

Backroom is source-available under PolyForm Noncommercial 1.0.0 (see [LICENSE](LICENSE)). Thank you for helping.

## Before you start

- For anything larger than a small fix, open an issue first so we can agree on the approach.
- Security problems do not go into issues; follow [SECURITY.md](SECURITY.md).
- Read [CLAUDE.md](CLAUDE.md): it holds the working rules, privacy rules and architecture rules that every change must respect. Requirements and architecture are in [docs/requirements.md](docs/requirements.md).

## Contributor License Agreement

Outside contributions need a signed [CLA](CLA.md), which lets the maintainer license contributions commercially as well. A bot comments on your first pull request with a link to sign through cla-assistant.io; the pull request cannot be merged before that. cla-assistant.io serves the agreement from a gist; `CLA.md` is the source and the gist must match it.

## Making a change

- One pull request, one behavior. Work on a branch named `feat/...`, `fix/...`, `chore/...`, `docs/...`, `test/...`, `ci/...`, `perf/...`, `sec/...` or `spike/...`.
- Everything in the repository is English; Turkish appears only in UI translation files.
- Every bug fix comes with a test that reproduces the bug. New behavior comes with tests, translations and accessibility checks.
- Run the checks from the repository root:

```powershell
dotnet build backend/Backroom.slnx
dotnet test --solution backend/Backroom.slnx

pnpm -C frontend install
pnpm -C frontend format:check
pnpm -C frontend lint
pnpm -C frontend typecheck
pnpm -C frontend test
pnpm -C frontend build
pnpm -C frontend check:bundle
```

## Dependencies

- Code we ship or link must be permissive (MIT, BSD, Apache-2.0, ISC, Zlib, Unicode and similar), transitive dependencies included. Weak copyleft (MPL-2.0) is allowed only as an unmodified transitive dependency and must be listed in the pull request when added. Build, test and CI tools, and services run as separate processes, are exempt. Strong copyleft (GPL, LGPL linked into shipped code, AGPL, EUPL), SSPL and other source-available licenses are never allowed in shipped code.
- Also avoid: MediatR v13+, AutoMapper v15+, MassTransit v9, ImageSharp, FluentAssertions v8+, Moq, Bitnami images, MinIO.
- State the license, latest release date and adoption of every new library in the pull request description.

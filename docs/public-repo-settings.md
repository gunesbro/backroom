# GitHub settings to switch on when the repository is public

Workflows are already hardened for fork pull requests (read-only tokens, no secrets, caches saved only from `main`,
no `pull_request_target`). The settings below live on GitHub and cannot be set from the repository. Menu paths are
under the repository's **Settings** tab.

## Branch ruleset for `main`

Rules > Rulesets > New ruleset > New branch ruleset. Target: default branch. Enforcement: Active.

- Restrict deletions and Block force pushes.
- Require linear history.
- Require a pull request before merging (at least 1 approval, dismiss stale approvals on new commits).
- Require conversation resolution before merging.
- Bypass list: add the **Repository admin** role with "For pull requests only", so the solo maintainer can merge
  their own pull requests after review.
- Require status checks to pass, and add exactly these gate jobs (one per workflow, they always run and pass when
  the heavy jobs were skipped): `backend-required`, `frontend-required`, `desktop-required`, `security-required`,
  `workflow-lint-required`. Do not require the individual jobs: path-based skipping and matrix job names would leave
  them pending forever.

## Actions

- Actions > General > **Approval for running fork pull request workflows from contributors**: **Require approval for
  all external contributors**.
- Actions > General > Workflow permissions: **Read repository contents and packages permissions**; leave
  **Allow GitHub Actions to create and approve pull requests** unchecked.
- Actions > General > Actions permissions: choose "Allow <owner>, and select non-<owner>, actions and reusable
  workflows" with only "Allow actions created by GitHub" ticked; every action in use is GitHub's own. Optionally tick
  "Require actions to be pinned to a full-length commit SHA".
- Caches are managed in the repository's **Actions** tab, under Management > Caches (not in Settings): delete any
  cache written before the hardening if a fork PR could have touched it.

## Security

- Advanced Security (Settings > Advanced Security): enable **Secret scanning** and **Push protection**.
- **Code scanning > CodeQL analysis > Set up > Default**: languages C#, JavaScript/TypeScript, Actions and Rust
  (Rust only if offered for your account; otherwise keep `cargo clippy` as the check). Query suite: Default.
- **Private vulnerability reporting**: Enable (Advanced Security). It backs `SECURITY.md`.
- **Dependabot**: enable Dependabot alerts and Dependabot security updates (Advanced Security). Version updates are
  already configured in `.github/dependabot.yml`.

## Community

- General > Features > **Discussions**: on. Use a "Licensing" category for commercial license questions.
- Contributor license agreement: cla-assistant.io takes the agreement as a GitHub Gist URL. Copy `CLA.md` (arriving
  in #103) into a public gist, keep it identical to `CLA.md`, then install the cla-assistant.io app
  (https://cla-assistant.io, sign in with GitHub, "Configure CLA", choose this repository, link the gist).
  The resulting `license/cla` check becomes a required status check once it has run.
- General > Pull Requests: allow squash merging only; enable "Automatically delete head branches".

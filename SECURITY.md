# Security policy

PC Checker runs with administrator rights and can change system settings,
remove software and install drivers, so we take security reports seriously.

## Supported versions

Only the latest release receives fixes. Please update before reporting.

## Reporting a vulnerability

Please do **not** open a public issue for a security problem.

Use GitHub's private reporting instead:
**Security → Report a vulnerability** on the
[repository page](https://github.com/Connor-Moyle/test-pcchecker/security/advisories/new).

Include:

- The version affected
- Steps to reproduce or a proof of concept
- What an attacker could achieve

You will get an acknowledgement within 7 days. Once a fix is ready we publish
a release and credit you in the changelog unless you prefer otherwise.

## What is in scope

- Ways to make PC Checker delete, change or install something the user did not
  confirm
- Path traversal or symlink tricks that escape the cleanup roots
- Command or script injection through program names, uninstall strings, file
  names or other data read from the system
- Leaking the Claude API key or other secrets from settings, logs or reports
- Update tampering (downloading or applying an update that was not published
  by this project)

## Design notes

- Secrets are protected with Windows DPAPI scoped to the current user; they are
  never written to logs or reports.
- Updates are fetched only from this repository's GitHub Releases.
- Cleanup never follows reparse points and refuses paths outside the category
  roots. See [docs/SAFETY.md](docs/SAFETY.md).
- The only network calls are: the update check, driver downloads through the
  Windows Update Agent, and (only when configured) the Claude API. See
  [docs/PRIVACY.md](docs/PRIVACY.md).

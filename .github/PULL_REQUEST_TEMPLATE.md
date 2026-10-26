## What this changes

<!-- One or two sentences. Link the issue if there is one: Fixes #123 -->

## Why

<!-- The problem this solves for users or maintainers. -->

## How it was tested

- [ ] `dotnet build PcChecker.sln -c Release` passes with no new warnings
- [ ] `dotnet test PcChecker.sln -c Release` passes
- [ ] Tested on Windows 11 (build: ) / Windows 10 22H2
- [ ] Checked the page in demo mode (`PcChecker.exe --demo`) if a page changed
- [ ] New or changed pure logic has unit tests

## Safety checklist (for changes that touch the system)

- [ ] Destructive or system-changing actions go through `IChangeGuard.RunGuardedAsync`
- [ ] The UI confirms with the user and explains what will happen
- [ ] Failures degrade gracefully (logged, partial data, no crash)
- [ ] Hardware features show their support level and never silently no-op
- [ ] Nothing new is written to logs or reports that could identify the user

## Notes for the reviewer

<!-- Screenshots for UI changes, follow-ups, open questions. -->

- [ ] `CHANGELOG.md` updated under *Unreleased*
- [ ] Docs updated (`docs/FEATURES.md`, `docs/SUPPORTED-HARDWARE.md`, `docs/FAQ.md`) if behaviour changed

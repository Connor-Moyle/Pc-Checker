# Changelog

All notable changes to PC Checker are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- Solution scaffold: contracts and models for every module, the WPF shell with
  Fluent navigation, shared controls and the Core test suite.
- Hardware model: `PhysicalDiskInfo.Partitions` now carries the partition list.
- Repository documentation, CI workflow and the release pipeline that publishes
  installer and portable builds to GitHub Releases.

### Changed
- API key protection uses `System.Security.Cryptography.ProtectedData`
  (DPAPI, current user).
- The application manifest declares Windows 10/11 compatibility only.

## [0.1.0] - Unreleased

First public preview. See the [feature overview](docs/FEATURES.md) for what
the app does.

[Unreleased]: https://github.com/Connor-Moyle/test-pcchecker/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Connor-Moyle/test-pcchecker/releases/tag/v0.1.0

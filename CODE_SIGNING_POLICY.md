# Code signing

## Current status

Corewise releases are **not code-signed yet**. Free signing for open-source projects (such as the
[SignPath Foundation](https://signpath.org/) program) requires a project with some public track record, which
Corewise does not have yet. I plan to apply again once it does. Until then, Windows SmartScreen shows a warning on
install, and Windows Smart App Control (when enabled) blocks the installer.

## How releases are built

Every release published on [GitHub Releases](https://github.com/acboer/Corewise-Monitor/releases) is built from the
tagged source in this repository by [GitHub Actions](.github/workflows/release.yml):

- `Corewise.exe` (dashboard and widget)
- `Corewise.Service.exe` (sensor service)
- `CorewiseSetup-<version>.exe` (installer)

Nothing built on a developer machine is published. Third-party libraries are bundled unmodified and keep their own
signatures (or none). The workflow already contains the signing steps; they stay inactive until a signing service
is set up.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [acboer](https://github.com/acboer) |
| Approvers | [acboer](https://github.com/acboer) |

Changes from people outside this list are reviewed by a committer before they are merged. All members use
multi-factor authentication on GitHub.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the
user. See the [privacy policy](PRIVACY.md) for what the local service shares with the dashboards you pair.

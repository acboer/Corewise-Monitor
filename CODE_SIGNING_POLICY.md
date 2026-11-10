# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

## What is signed

Every release of Corewise published on [GitHub Releases](https://github.com/acboer/Corewise-Monitor/releases):

- `Corewise.exe` (dashboard and widget)
- `Corewise.Service.exe` (sensor service)
- `CorewiseSetup-<version>.exe` (installer)

Releases are built from the tagged source in this repository by
[GitHub Actions](.github/workflows/release.yml) and submitted to SignPath from that build. Nothing built on a
developer machine is signed. Third-party libraries are bundled unmodified and keep their own signatures (or none).

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [acboer](https://github.com/acboer) |
| Approvers | [acboer](https://github.com/acboer) |

Changes from people outside this list are reviewed by a committer before they are merged. Every signing request is
approved manually. All members use multi-factor authentication on GitHub and SignPath.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the
user. See the [privacy policy](PRIVACY.md) for what the local service shares with the dashboards you pair.

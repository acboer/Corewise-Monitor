# SignPath configuration

Copies of the artifact configurations used for signing, to paste in SignPath (Project > Artifact configurations).
Slugs must match the ones in `.github/workflows/release.yml`:

| Slug | File | What it signs |
|---|---|---|
| `executaveis` | [executaveis.xml](executaveis.xml) | `Corewise.exe`, `Corewise.Service.exe` before they go into the installer |
| `instalador` | [instalador.xml](instalador.xml) | `CorewiseSetup-<version>.exe` |

Repository settings used by the workflow (Settings > Secrets and variables > Actions):

- Secret `SIGNPATH_API_TOKEN`: API token of the CI submitter user.
- Variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG` (`release-signing`).

Note: Inno Setup pads the version strings of the installer with spaces. If SignPath rejects the installer's
`product-version`, test the restriction in the SignPath editor before changing anything else.

# Releasing DotNetJq

Releases are tag-driven and publish only after all build, correctness, platform,
package, source, license, checksum, and relink gates pass.

## One-time repository and account setup

Configure a protected GitHub environment named `release` with required reviewer
approval. The release workflow expects:

| Name | Kind | Purpose |
|---|---|---|
| `NUGET_USER` | environment variable | nuget.org username owning the trusted-publishing policy |
| `HOMEBREW_TAP_OWNER` | environment variable | owner of the project tap, for example `gtg-open` |
| `HOMEBREW_TAP_NAME` | environment variable | tap repository name, for example `homebrew-tap` |
| `HOMEBREW_APP_ID` | environment variable | GitHub App ID with contents write access to the tap |
| `HOMEBREW_APP_PRIVATE_KEY` | environment secret | GitHub App private key |
| `WINGET_CREATE_GITHUB_TOKEN` | environment secret | dedicated classic token accepted by `wingetcreate` |

On nuget.org, create a trusted-publishing policy for owner `gtg-open`, repository
`dotnetjq`, workflow file `release-cli.yml`, environment `release`, and all ten
package IDs (`DotNetJq.Library`, `dotnetjq`, and `dotnetjq.<RID>`). No NuGet API
key is stored in GitHub.

Create `gtg-open/homebrew-tap` separately, add `Formula/dotnetjq.rb`, and install
the GitHub App only on `dotnetjq` and `homebrew-tap`. The source repository's
ordinary `GITHUB_TOKEN` cannot write another repository.

For WinGet, use a dedicated GitHub account that has accepted any required CLA.
Its classic token must have the scope required by the pinned `wingetcreate`
client to fork and open a PR against `microsoft/winget-pkgs`. Fine-grained token
support must not be assumed until the official client documents it.

Enable GitHub private vulnerability reporting, protect `main` and `v*` tags,
require the semantic, parser-generation, and cross-platform checks, and restrict
tag creation and environment approval to maintainers. These are repository
settings and are intentionally not changed by source automation.

## Prepare a release

1. Update `DotNetJqVersion` in `Directory.Build.props`. Update jq compatibility
   independently only when the port and manifest actually move upstream.
2. Run `tools/verify-release.sh` with the pinned test assets.
3. Run and review the complete performance suite; update the certified report
   only from complete, correctness-passing evidence. The tag workflow repeats
   all 879 fixture scenarios and 21 sustained workloads and blocks publication
   when the same-run ratios exceed `tools/performance/release-thresholds.json`.
4. Merge through protected `main` and wait for every required workflow.
5. Create and push an annotated protected tag, for example `v1.0.0` or
   `v1.1.0-rc.1`.

The workflow validates the tag against the declared source version before it
publishes. Stable tags update GitHub Releases and NuGet, open a Homebrew tap PR,
and submit WinGet. The Homebrew formula becomes installable after that PR is
reviewed and merged. Prerelease tags publish only GitHub prerelease assets and
NuGet prerelease packages.

Never move a published tag, replace an asset, or reuse a package version. A
partially completed publication must be inspected before retrying. NuGet rejects
an already-present version instead of silently accepting it, while every
existing draft asset is downloaded and byte-compared with the locally verified
bundle before package publication.

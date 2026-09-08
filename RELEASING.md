# Releasing DotNetJq

Releases are tag-driven and publish only after all build, correctness, platform,
package, source, license, checksum, and relink gates pass.

## One-time repository and account setup

Configure a protected GitHub environment named `release` with required reviewer
approval. The release workflow expects:

| Name | Kind | Purpose |
|---|---|---|
| `NUGET_USER` | environment variable | individual nuget.org profile username used by `NuGet/login`, for example `gtg-open-maintainer` |
| `HOMEBREW_TAP_OWNER` | environment variable | owner of the project tap, for example `gtg-open` |
| `HOMEBREW_TAP_NAME` | environment variable | `homebrew-dotnetjq`, the separate project tap repository |
| `HOMEBREW_APP_ID` | environment variable | GitHub App ID with contents and pull-request write access to the tap |
| `HOMEBREW_APP_PRIVATE_KEY` | environment secret | GitHub App private key |
| `WINGET_CREATE_GITHUB_TOKEN` | environment secret | dedicated classic token with the `public_repo` scope accepted by `wingetcreate` |
| `RELEASE_SETTINGS_GITHUB_TOKEN` | environment secret | fine-grained token restricted to `gtg-open/dotnetjq` with repository **Administration: Read-only**, used only to prove immutable releases are enabled |

On nuget.org, first confirm that the `gtg-open` organization has a working,
verified organization email address. Do not use an unverified placeholder. Then
create a trusted-publishing policy with these coordinates:

- policy owner: organization `gtg-open`;
- repository owner: `gtg-open`;
- repository: `dotnetjq`;
- workflow file: `release-cli.yml` (the file name only);
- environment: `release`;
- scopes: publishing new packages and publishing new versions; and
- package selection covering `DotNetJq.Library`, `dotnetjq`, and all eight
  `dotnetjq.<RID>` packages from `packaging/release-targets.tsv`.

Set `NUGET_USER` to the individual profile name `gtg-open-maintainer`, not the
organization name or an email address. That user must remain an active member of
`gtg-open`; the policy itself remains owned by the organization. No NuGet API key
is stored in GitHub.

The public [gtg-open/homebrew-dotnetjq](https://github.com/gtg-open/homebrew-dotnetjq)
tap uses protected `main` and merge-commit pull requests. Do not add a
placeholder `Formula/dotnetjq.rb`; the first stable release opens the pull
request that adds the verified, checksum-bound formula. Configure the GitHub App
with **Contents: Read and write** and **Pull requests: Read and write**, and
install it only on `gtg-open/homebrew-dotnetjq`. The source checkout uses the source
repository's ordinary `GITHUB_TOKEN`; the App token is needed only because that
token cannot write the separate tap repository.

The tap's required `Homebrew tap gate` checks formula provenance against the
exact `dotnetjq.rb` asset from a published immutable source release before
executing Ruby, then installs and tests the package on macOS/Linux x64/ARM64.
Until the first stable release, it tests the verification tooling and tap
discovery on all four platforms; it explicitly does not claim an installation
test without a published package. Formula updates remain separate from source
history and must pass tap CI before a maintainer merges them.

For WinGet, use a dedicated GitHub account that has accepted any required CLA.
Create a classic personal access token with the `public_repo` scope so the pinned
`wingetcreate` client can create or update the account's public fork and open a
PR against `microsoft/winget-pkgs`. Do not grant the broader `repo` scope.
Fine-grained token support must not be assumed until the official client
documents it.

Enable GitHub private vulnerability reporting, protect `main` and `v*` tags,
require the semantic, parser-generation, and cross-platform checks, and restrict
tag creation and environment approval to maintainers. These are repository
settings and are intentionally not changed by source automation.

In **Settings → General → Releases**, enable **Release immutability** before the
first release. GitHub applies the protection only to releases published after
the setting is enabled. The protected publication job queries GitHub's
repository setting with `RELEASE_SETTINGS_GITHUB_TOKEN` and stops before NuGet
or GitHub publication unless the API reports `enabled: true`. Keep this token
separate from the publication token: it needs Administration read access only,
not write access. See [Preventing changes to your releases](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes).

## Prepare a release

1. Update `DotNetJqVersion` in `Directory.Build.props`. Update jq compatibility
   independently only when the port and manifest actually move upstream.
2. Run `tools/verify-release.sh` with the pinned test assets.
3. Run and review the complete performance suite; update the certified report
   only from complete, correctness-passing evidence. The tag workflow repeats
   all 879 fixture scenarios and 21 sustained workloads with candidate and
   freshly rebuilt, pinned DotNetJq baseline interleaved on the same runner.
   Publication is blocked when candidate/baseline ratios exceed
   `tools/performance/release-thresholds.json`; historical workstation timings
   are not CI limits. Native jq comparisons remain in the reports.
4. Merge through protected `main` and wait for every required workflow.
5. Create and push an annotated protected tag, for example `v1.0.0` or
   `v1.1.0-rc.1`.

The workflow validates the tag against the declared source version before it
publishes. Every source checkout explicitly selects the triggering commit SHA,
so `actions/checkout` does not replace an annotated local tag with its peeled
commit. Full-history publication checkouts retain the real tag object, and the
unchanged tag validator compares that object and commit with the remote before
each publication boundary. Regression tests reproduce both checkout fetch paths
and require the SHA input on every release source checkout.

The workflow generates a deterministic release body and requires every existing
draft or published release to retain that exact body. It then stages and
byte-verifies a private GitHub draft, and one protected
`publish-core` job publishes or safely resumes all ten NuGet packages and makes
that exact draft public without a second approval boundary between those two
irreversible operations. Stable tags then open a Homebrew tap PR and submit
WinGet. The Homebrew formula becomes installable after that PR is reviewed and
merged. Prerelease tags publish only GitHub prerelease assets and NuGet
prerelease packages.

Never move a published tag, replace an asset, or reuse a package version. A
retry preflights all ten exact NuGet ID/version pairs. It skips a present package
only after verifying its NuGet.org repository signature is owned by `gtg-open`,
its identity, and its complete decompressed payload against the local plan; it
publishes only proven-absent rows and rechecks ambiguous responses before a
bounded retry. Every existing GitHub draft or published release is likewise
downloaded and byte-compared with the locally verified 27-asset bundle. An
interrupted draft whose existing assets form an exact byte-verified subset is
completed without overwriting anything; a published release must already
contain all 27 assets. The title, jq compatibility label, and release body are
also compared exactly on every classification and post-publication verification.
Every already-published release must additionally report `immutable: true` from
GitHub's API; this rejects releases published before immutability was enabled.
Any mismatch, extra asset, or unproven state stops the workflow. Rerunning an
older stable tag never marks it Latest and does not open
Homebrew or WinGet downgrade submissions.

The Homebrew job classifies the tap's current formula before copying the
candidate: an identical or newer valid formula is a no-op, while malformed,
ambiguous, or same-version-different content fails closed. The WinGet job first
checks whether the exact three manifests are already present on the canonical
default branch. If not, it checks the submitting account's open
`microsoft/winget-pkgs` pull requests through the repository's direct issue
listing, filtered by the authenticated creator, rather than the eventually
consistent search index. It reuses a candidate only when the title, fork branch,
three changed paths, and all three Git blob payloads exactly match the generated
manifests. Ambiguous or mismatched state stops the job.

# Versioning and releases

DotNetJq uses Semantic Versioning for its own API and distribution lifecycle.
Upstream jq compatibility is stated independently.

Example release identity:

```text
Package/tag:          1.0.0 / v1.0.0
Release title:        DotNetJq 1.0.0 — jq 1.8.2 compatible
AssemblyVersion:      1.0.0.0
FileVersion:          1.0.0.<CI build number>
InformationalVersion: 1.0.0+jq.1.8.2.build.<run>.sha.<commit>
```

NuGet and CLI versions remain normal SemVer such as `1.0.0` or
`1.1.0-rc.1`. Build metadata records provenance without changing package
precedence. `DotNetJqVersionInfo` exposes the exact jq release and commit to
library consumers.

## Branches

Development uses protected `main` and pull requests. Release branches are
created lazily only when an older major line requires maintenance alongside a
newer line. A contributor normally opens a PR from any branch in a fork; the
branch does not need to exist in this repository.

## Publication contract

Pushes and pull requests build and test but never publish. A protected `v*` tag
whose version matches the source declaration starts the release workflow. It:

1. runs the complete semantic release gate;
2. builds and tests eight NativeAOT archives and ten NuGet packages;
3. verifies installed packages on all eight target environments and rejects
   material performance regressions against a pinned DotNetJq baseline built
   and measured alongside the candidate on the same runner;
4. verifies checksums, exact package-manager metadata, corresponding source,
   and the modified-component LGPL relink proof;
5. creates attestations, stages a GitHub release, publishes NuGet packages in
   dependency order, then makes the GitHub release public;
6. for stable versions only, opens a Homebrew tap pull request and submits
   WinGet manifests.

Release candidates are GitHub prereleases and NuGet prerelease packages. They
do not update stable Homebrew or WinGet channels.

GitHub Releases use the workflow's short-lived token. NuGet uses trusted
publishing/OIDC rather than a long-lived API key. Homebrew uses a narrowly
installed GitHub App for the project tap. WinGet submission currently requires
a dedicated classic GitHub token accepted by `wingetcreate`; Microsoft reviews
and merges the resulting PR.

The one-time account/environment setup is documented in
[RELEASING.md](../RELEASING.md). Never retag or replace published assets; issue a
new SemVer release for every correction, including security fixes.

# Security policy

## Supported versions

Until 1.0.0 is published, no public release is supported. After publication,
the latest stable release receives security fixes. Older lines are supported
only when the release notes say so.

## Report a vulnerability

Do not open a public issue. Use
[GitHub private vulnerability reporting](https://github.com/gtg-open/dotnetjq/security/advisories/new)
to contact the maintainers privately. Include:

- affected DotNetJq version, package, RID, OS, and .NET version;
- a minimal jq filter and input when safe to share;
- impact, expected behavior, logs, and relevant artifact hashes;
- whether upstream jq, Oniguruma, glibc, or the .NET runtime may also be affected.

If the private-report form is unavailable, contact a repository maintainer
privately through GitHub before disclosing technical details. Do not send
secrets or personal data in a report.

Maintainers will acknowledge a usable report, investigate affected versions,
coordinate with upstream projects where appropriate, and agree on a disclosure
timeline with the reporter. This project does not currently offer a bug bounty.

## Security boundary

DotNetJq's timeouts, cancellation, transition counts, output limits, and
filesystem capabilities are cooperative controls; they are not a hard sandbox.
Compilation and intermediate allocations are not governed by a universal memory
quota, and host callbacks must be bounded by the host. Review
[the security/resource audit](porting/SECURITY_RESOURCE_AUDIT.md) before running
untrusted filters or inputs.

Security releases use a new immutable version and tag. They run the complete
compatibility, platform, package, source, and relink gates, publish affected and
fixed ranges, and keep version-matched corresponding source available beside
every NativeAOT binary.

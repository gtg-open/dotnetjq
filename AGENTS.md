# DotNetJq Agent Rules

1. Upstream jq-1.8.2 commit 34f7186b86743a083a589741b6cea95293524108 is the semantic source of truth.
2. Never redesign the compatibility core without an explicit task.
3. Preserve upstream file mappings and function names.
4. Read porting/PORTING_MANIFEST.json before making changes.
5. Every upstream file must be PORT, PROXY, GENERATED, REUSE, or OMITTED.
6. Never delete a mapped symbol merely because it appears unused until upstream behavior/tests confirm it is unnecessary.
7. Proxies must remain behind jq-shaped compatibility interfaces.
8. Official jq tests and differential execution are the correctness oracle.
9. Never weaken or delete a failing compatibility test to make the build green.
10. Never shell out to jq in production code.
11. The official jq binary may be used only in tests as an oracle.
12. Update manifest/status/comments in the same change as implementation.
13. Prefer small file/function-local fixes over broad refactors.
14. Build and run relevant tests after every porting task.
15. Before claiming completion, run the complete applicable compatibility suite.
16. Before every commit or publication, inspect the exact staged diff and generated outputs, including filenames, symlink targets, metadata, packages, symbols, logs, and documentation. Never expose personal or private information, local machine paths, credentials, or private connection setup. Use repository-relative paths, explicit environment configuration, and generic placeholders. Keep private audit evidence outside tracked and published content; retain legitimate third-party attribution, licenses, and approved public maintainer identities. Run the repository privacy check when available, and resolve its findings before committing or publishing.

The externally downloaded reference checkout is normally available at `upstream/jq`, relative to the repository root, or through `DOTNETJQ_UPSTREAM`. The library must build and run when it is absent.

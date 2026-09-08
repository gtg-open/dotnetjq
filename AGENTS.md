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

The externally downloaded reference checkout is normally available at `upstream/jq`, relative to the repository, or through `DOTNETJQ_UPSTREAM`. The library must build and run when it is absent.

Before every commit or publication, inspect the exact staged contents and any generated outputs, including filenames, symlink targets, archives, debug information, and commit/tag metadata. Never expose personal or private information, workstation paths, credentials, or private connection setup. Use repository-relative paths, documented environment configuration, and generic examples. Run the repository privacy check before committing and before publishing generated artifacts. Keep private audit evidence outside tracked repositories and public comments. Preserve legitimate third-party attribution, licenses, and approved public maintainer identities. Never restore removed history from an old checkout or backup.

Publication safety protocol

1. Before creating a commit, every agent change must include a local review of exactly what is being changed (including generated files): `git diff --name-only` and `git diff --cached --name-only`.
2. Before pushing or proposing publication, run all three checks and confirm each exits clean:
   - `python3 -B tools/privacy/check.py --staged`
   - `python3 -B tools/privacy/check.py --worktree`
   - `python3 -B tools/privacy/check.py --metadata --history`
3. For any package/artifact output path (for example, `artifacts/`, `out/`, `nupkg`, `zip`, `tar.gz`, or similar), run scanner with an explicit output scope and include that command in release notes/logs before it is uploaded.
4. If any scan finds a hit, treat it as a blocker. Do not merge, amend, or push until the hit is removed and all three checks are rerun.
5. This is a hard policy in addition to the Python checker: agents must also verify that private files are not introduced by name or path (`private`, `secret`, user home/workstation paths), and that symlinks and binary-like bundles do not include forbidden metadata.

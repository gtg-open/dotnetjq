# Upstream oracle validation

Validation date: 2026-09-05.

The compatibility oracle is jq 1.8.2. Two independent jq upstream artifacts were
validated. Paths below are portable examples relative to the repository root:

- Git checkout: `upstream/jq`, tag `jq-1.8.2`, commit
  `34f7186b86743a083a589741b6cea95293524108`, with Oniguruma submodule commit
  `4ef89209a239c1aea328cf13c05a2807e5c146d1`.
- Official release source archive:
  `upstream/jq-1.8.2.tar.gz`, downloaded from the jq 1.8.2 GitHub
  release, SHA-256
  `71b8d6e8f5fe81f6c6d0d110e3892251f6ce76ed095abd315e26e6e1193af3af`.
  The source can be extracted and built at `upstream/jq-1.8.2-release`.

The release source was configured with its bundled Oniguruma implementation:

```text
./configure --with-oniguruma=builtin
make -j8
make check -j8
```

The jq testsuite completed with 9/9 test groups passing and no skips, expected
failures, failures, or errors:

```text
optionaltest  PASS
base64test    PASS
uritest       PASS
onigtest      PASS
manonigtest   PASS
mantest       PASS
utf8test      PASS
jqtest        PASS
shtest        PASS
```

The bundled Oniguruma checks also passed: 7/7 library tests and 14/14 sample
tests. The resulting source-built `jq` reports `jq-1.8.2` and has SHA-256
`97ef955205ff660034ec87933f4222882b952131c8a8f952fa536895630c2c47`.

The differential runner uses the separately installed official jq 1.8.2
release binary, provisioned at `artifacts/test-assets/jq-1.8.2/oracle/jq` or supplied
through `DOTNETJQ_ORACLE`. Its SHA-256 is
`b1c22172dd303f3be49e935aa56aa48a8b7a46e0bc838b4997d3bb451495870f`.
Production DotNetJq code does not invoke either oracle.

The GNU C Library reference used for the separately licensed managed gamma
component can be checked out at `upstream/glibc-2.39`. It is pinned to
annotated tag object `9609a435f3f9a07c1cf607ad5821b12f735abd69`, peeled release
commit `ef321e23c20eebc6d6fb4044425c00e6df27b05f`. This checkout is reference
material only; the package contains the component's complete corresponding
managed source and does not require the checkout to build or run.

# Oniguruma retry-limit oracle

This isolated helper freezes `retry_limit_in_match` behavior from the jq-1.8.2
semantic source checkout at commit
`34f7186b86743a083a589741b6cea95293524108`. It performs one anchored
`onig_match_with_param` call and reports the native result and error text.
Compilation uses jq's exact `ONIG_OPTION_CAPTURE_GROUP` and
`ONIG_SYNTAX_PERL_NG` settings, so the grammar-boundary rows exercise the same
syntax and capture policy as jq's `f_match`.

Build and run it against the pinned checkout:

```sh
cc -O2 -I upstream/jq/vendor/oniguruma/src \
  tools/regex-retry-oracle/onig-retry-oracle.c \
  upstream/jq/vendor/oniguruma/src/.libs/libonig.a \
  -o /tmp/onig-retry-oracle

/tmp/onig-retry-oracle 10000000 \
  '\A'a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?a?'(?!)' \
  aaaaaaaaaaaaaaaaaaaaaaaa

/tmp/onig-retry-oracle 10000000 '\Aa*(?!)' @a:9999999
```

The checked-in [`corpus.tsv`](corpus.tsv) records the exact small-count and
default-limit boundaries. The explicit optional chain is intentional:
Oniguruma compiles `(?:a?){N}` differently for larger `N`. With `N` explicit
optionals, every one of the `2^N` paths reaches the always-failing negative
lookahead `(?!)` once. Therefore 23
optionals finish with 8,388,608 retries, while 24 optionals reach the pinned
10,000,000 limit and return `ONIGERR_RETRY_LIMIT_IN_MATCH_OVER` (`-17`).

`LIMIT=0` is the native unlimited form. The managed compatibility test also
freezes the fact that the retry counter is reset for each match-at candidate;
it is not a wall-clock timeout or a search-global work budget.

The remaining corpus rows freeze the native failure-pop boundary for valid
Perl-NG calls, multiplex backreferences, capture and arbitrary-regex
conditionals, nested absent ranges, class-context quote escapes, reduced and
fixed-width lookbehinds, and capture-level backreferences/conditions. Each row
records the limit at which Oniguruma returns error `-17`; the managed theory
also proves that limit plus one completes as a mismatch.

For linear boundaries, `@a:N` asks the helper to allocate `N` ASCII `a` bytes
locally, avoiding the operating system's command-line size limit. Native
`\Aa*(?!)` performs `N+1` failure pops: `N=9,999,998` is a mismatch below the
default, while `N=9,999,999` reaches 10,000,000 and returns error `-17`.

/*
 * Frozen-oracle helper for jq's pinned Oniguruma retry semantics.
 *
 * Build against the jq-1.8.2 checkout at commit
 * 34f7186b86743a083a589741b6cea95293524108:
 *
 *   cc -O2 -I "$JQ_UPSTREAM/vendor/oniguruma/src" \
 *     onig-retry-oracle.c \
 *     "$JQ_UPSTREAM/vendor/oniguruma/src/.libs/libonig.a" \
 *     -o onig-retry-oracle
 *
 * Usage: onig-retry-oracle LIMIT PATTERN INPUT
 * INPUT may be @a:N to generate N ASCII 'a' bytes without an argv-size limit.
 * LIMIT=0 means unlimited, exactly as in Oniguruma. The program performs one
 * anchored match-at attempt, not a search, so the retry counter has one native
 * match lifetime.
 */

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "oniguruma.h"

static void print_error(int code, OnigErrorInfo *error_info) {
  UChar message[ONIG_MAX_ERROR_MESSAGE_LEN];
  if (error_info != NULL)
    onig_error_code_to_str(message, code, error_info);
  else
    onig_error_code_to_str(message, code);
  printf("error\t%d\t%s\n", code, (const char *)message);
}

int main(int argc, char **argv) {
  char *limit_end;
  unsigned long limit;
  int result;
  regex_t *regex;
  OnigErrorInfo error_info;
  OnigMatchParam *match_param;
  OnigRegion *region;
  UChar *pattern;
  UChar *input;
  UChar *input_end;
  int generated_input;
  OnigEncoding encodings[] = {ONIG_ENCODING_UTF8};

  if (argc != 4) {
    fprintf(stderr, "usage: %s LIMIT PATTERN INPUT\n", argv[0]);
    return 64;
  }

  errno = 0;
  limit = strtoul(argv[1], &limit_end, 10);
  if (errno != 0 || *argv[1] == '\0' || *limit_end != '\0') {
    fprintf(stderr, "invalid limit: %s\n", argv[1]);
    return 64;
  }

  result = onig_initialize(encodings, 1);
  if (result != ONIG_NORMAL) {
    print_error(result, NULL);
    return 65;
  }

  pattern = (UChar *)argv[2];
  generated_input = 0;
  if (strncmp(argv[3], "@a:", 3) == 0) {
    char *length_end;
    unsigned long long length;
    errno = 0;
    length = strtoull(argv[3] + 3, &length_end, 10);
    if (errno != 0 || argv[3][3] == '\0' || *length_end != '\0' ||
        length > (unsigned long long)SIZE_MAX - 1) {
      fprintf(stderr, "invalid generated input length: %s\n", argv[3]);
      onig_end();
      return 64;
    }

    input = (UChar *)malloc((size_t)length + 1);
    if (input == NULL) {
      fprintf(stderr, "allocation failed\n");
      onig_end();
      return 70;
    }

    memset(input, 'a', (size_t)length);
    input[length] = '\0';
    input_end = input + length;
    generated_input = 1;
  }
  else {
    input = (UChar *)argv[3];
    input_end = input + strlen((const char *)input);
  }
  result = onig_new(&regex,
                    pattern,
                    pattern + strlen((const char *)pattern),
                    ONIG_OPTION_CAPTURE_GROUP,
                    ONIG_ENCODING_UTF8,
                    ONIG_SYNTAX_PERL_NG,
                    &error_info);
  if (result != ONIG_NORMAL) {
    print_error(result, &error_info);
    if (generated_input) free(input);
    onig_end();
    return 66;
  }

  match_param = onig_new_match_param();
  region = onig_region_new();
  if (match_param == NULL || region == NULL) {
    fprintf(stderr, "allocation failed\n");
    onig_region_free(region, 1);
    onig_free_match_param(match_param);
    onig_free(regex);
    if (generated_input) free(input);
    onig_end();
    return 70;
  }

  onig_set_retry_limit_in_match_of_match_param(match_param, limit);
  result = onig_match_with_param(regex,
                                 input,
                                 input_end,
                                 input,
                                 region,
                                 ONIG_OPTION_NONE,
                                 match_param);
  if (result >= 0)
    printf("match\t%d\n", result);
  else if (result == ONIG_MISMATCH)
    printf("mismatch\t%d\n", result);
  else
    print_error(result, NULL);

  onig_region_free(region, 1);
  onig_free_match_param(match_param);
  onig_free(regex);
  if (generated_input) free(input);
  onig_end();
  return 0;
}

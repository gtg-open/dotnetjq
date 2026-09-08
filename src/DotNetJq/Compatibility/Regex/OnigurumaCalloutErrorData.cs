// DOTNETJQ COMPATIBILITY PORT
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Oniguruma revision: 4ef89209a239c1aea328cf13c05a2807e5c146d1
// Source: vendor/oniguruma/src/regerror.c (onig_error_code_to_format and
// onig_is_error_code_needs_param), vendor/oniguruma/src/oniguruma.h.
// Strategy: PORT -- exact message lookup used by the managed built-in ERROR callout.

namespace DotNetJq.Compatibility.Regex;

internal static class OnigurumaCalloutErrorData
{
    internal static string Format(int code) => code switch
    {
        -1 => "mismatch",
        -2 => "no support in this configuration",
        -3 => "abort",
        -5 => "fail to memory allocation",
        -6 => "undefined type (bug)",
        -11 => "internal parser error (bug)",
        -12 => "stack error (bug)",
        -13 => "undefined bytecode (bug)",
        -14 => "unexpected bytecode (bug)",
        -15 => "match-stack limit over",
        -16 => "parse depth limit over",
        -17 => "retry-limit-in-match over",
        -18 => "retry-limit-in-search over",
        -19 => "subexp-call-limit-in-search over",
        -21 => "default multibyte-encoding is not set",
        -22 => "can't convert to wide-char on specified multibyte-encoding",
        -23 => "fail to initialize",
        -30 => "invalid argument",
        -100 => "end pattern at left brace",
        -101 => "end pattern at left bracket",
        -102 => "empty char-class",
        -103 => "premature end of char-class",
        -104 => "end pattern at escape",
        -105 => "end pattern at meta",
        -106 => "end pattern at control",
        -108 => "invalid meta-code syntax",
        -109 => "invalid control-code syntax",
        -110 => "char-class value at end of range",
        -111 => "char-class value at start of range",
        -112 => "unmatched range specifier in char-class",
        -113 => "target of repeat operator is not specified",
        -114 => "target of repeat operator is invalid",
        -115 => "nested repeat operator",
        -116 => "unmatched close parenthesis",
        -117 => "end pattern with unmatched parenthesis",
        -118 => "end pattern in group",
        -119 => "undefined group option",
        -120 => "invalid group option",
        -121 => "invalid POSIX bracket type",
        -122 => "invalid pattern in look-behind",
        -123 => "invalid repeat range {lower,upper}",
        -200 => "too big number",
        -201 => "too big number for repeat range",
        -202 => "upper is smaller than lower in repeat range",
        -203 => "empty range in char class",
        -204 => "mismatch multibyte code length in char-class range",
        -205 => "too many multibyte code ranges are specified",
        -206 => "too short multibyte code string",
        -207 => "too big backref number",
        -208 => "invalid backref number/name",
        -209 => "numbered backref/call is not allowed. (use name)",
        -210 => "too many captures",
        -212 => "too long wide-char value",
        -213 => "undefined operator",
        -214 => "group name is empty",
        -215 => "invalid group name <%n>",
        -216 => "invalid char in group name <%n>",
        -217 => "undefined name <%n> reference",
        -218 => "undefined group <%n> reference",
        -219 => "multiplex defined name <%n>",
        -220 => "multiplex definition name <%n> call",
        -221 => "never ending recursion",
        -222 => "group number is too big for capture history",
        -223 => "invalid character property name {%n}",
        -224 => "invalid if-else syntax",
        -225 => "invalid absent group pattern",
        -226 => "invalid absent group generator pattern",
        -227 => "invalid callout pattern",
        -228 => "invalid callout name",
        -229 => "undefined callout name",
        -230 => "invalid callout body",
        -231 => "invalid callout tag name",
        -232 => "invalid callout arg",
        -400 => "invalid code point value",
        -401 => "too big wide-char value",
        -402 => "not supported encoding combination",
        -403 => "invalid combination of options",
        -404 => "undefined error code", // Defined in oniguruma.h; no regerror.c case.
        -405 => "undefined error code", // Defined in oniguruma.h; no regerror.c case.
        -406 => "very inefficient pattern",
        -500 => "library is not initialized",
        _ => "undefined error code",
    };

    internal static bool NeedsParameter(int code) => code is
        -215 or -216 or -217 or -218 or -219 or -220 or -223;
}

// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_private.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_private.h
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv_private.h.cs
//
// Rules:
// - Preserve upstream function names wherever possible.
// - Behavioral compatibility is defined by upstream tests/differential tests.
// - Do not refactor cross-file architecture during compatibility port.
//
// Substitutions:
// - Managed jv numeric payloads replace direct access to the C union/decNumber payload.
// - The C int predicate result is represented by bool, following the existing managed jv API.
//
// Known differences:
// - C int predicates are managed bools; literal/literal comparison still uses exact decNumber semantics.

namespace DotNetJq.Port;

internal static partial class libjq
{
    internal static int jvp_number_cmp(jv left, jv right)
    {
        ensure_number(left, nameof(left));
        ensure_number(right, nameof(right));

        var leftNumber = (JvNumber)left.Value!;
        var rightNumber = (JvNumber)right.Value!;
        if (leftNumber.ExactValue is { } leftExact &&
            rightNumber.ExactValue is { } rightExact)
        {
            return Math.Sign(leftExact.CompareTo(rightExact));
        }

        var leftValue = leftNumber.Value;
        var rightValue = rightNumber.Value;
        if (leftValue < rightValue)
        {
            return -1;
        }

        return leftValue == rightValue ? 0 : 1;
    }

    internal static bool jvp_number_is_nan(jv number)
    {
        ensure_number(number, nameof(number));
        var value = (JvNumber)number.Value!;
        // jq-1.8.2 tests an allocated literal's embedded decNumber directly.
        // Do not ask for NumberValue here: that would populate the lazy
        // binary64 cache before jv_print.c has a chance to emit the exact
        // finite literal.
        return value.ExactValue is { } exact
            ? exact.IsNaN
            : double.IsNaN(value.Value);
    }

    private static void ensure_number(jv value, string parameterName)
    {
        if (value.Kind != jv_kind.JV_KIND_NUMBER)
        {
            throw new ArgumentException("Expected a jq number value.", parameterName);
        }
    }
}

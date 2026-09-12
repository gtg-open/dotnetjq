// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: vendor/decNumber/decContext.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/vendor/decNumber/decContext.c
// Strategy: PROXY
// Target file: src/DotNetJq/Port/vendor/decNumber/decContext.c.cs
// Upstream copyright notice: Copyright (c) IBM Corporation, 2000, 2009. All rights reserved.
// Upstream license: ICU License -- ICU 1.8.1 and later; complete terms are in /COPYING.jq.
// UPSTREAM COMPONENT: IBM decNumber decimal-context initialization, status, traps, and endian helpers.
// REPLACEMENT: managed context state and ArithmeticException traps.
// WHY: managed state replaces native structs and signal-based traps while preserving jq's decimal contract.
// BEHAVIORAL CONTRACT: preserve context defaults, status bit operations, trap decisions, and status strings.
// KNOWN DIFFERENCES: endian testing reports the managed runtime architecture.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests, and jq numeric fixtures.

namespace DotNetJq.Port;

internal static partial class libjq
{
    private const string DEC_Condition_CS = "Conversion syntax";
    private const string DEC_Condition_DZ = "Division by zero";
    private const string DEC_Condition_DI = "Division impossible";
    private const string DEC_Condition_DU = "Division undefined";
    private const string DEC_Condition_IE = "Inexact";
    private const string DEC_Condition_IS = "Insufficient storage";
    private const string DEC_Condition_IC = "Invalid context";
    private const string DEC_Condition_IO = "Invalid operation";
    private const string DEC_Condition_OV = "Overflow";
    private const string DEC_Condition_PA = "Clamped";
    private const string DEC_Condition_RO = "Rounded";
    private const string DEC_Condition_SU = "Subnormal";
    private const string DEC_Condition_UN = "Underflow";
    private const string DEC_Condition_ZE = "No status";
    private const string DEC_Condition_MU = "Multiple status";

    internal static decContext decContextClearStatus(decContext context, uint mask)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.status &= ~mask;
        return context;
    }

    internal static decContext decContextDefault(decContext context, int kind)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.digits = 9;
        context.emax = DEC_MAX_EMAX;
        context.emin = DEC_MIN_EMIN;
        context.round = rounding.DEC_ROUND_HALF_UP;
        context.traps = DEC_Errors;
        context.status = 0;
        context.clamp = 0;

        switch (kind)
        {
            case DEC_INIT_BASE:
                break;
            case DEC_INIT_DECIMAL32:
                SetIeeeContext(context, 7, 96, -95);
                break;
            case DEC_INIT_DECIMAL64:
                SetIeeeContext(context, 16, 384, -383);
                break;
            case DEC_INIT_DECIMAL128:
                SetIeeeContext(context, 34, 6144, -6143);
                break;
            default:
                decContextSetStatus(context, DEC_Invalid_operation);
                break;
        }

        return context;
    }

    internal static rounding decContextGetRounding(decContext context) => context.round;

    internal static uint decContextGetStatus(decContext context) => context.status;

    internal static decContext decContextRestoreStatus(decContext context, uint newStatus, uint mask)
    {
        context.status = (context.status & ~mask) | (newStatus & mask);
        return context;
    }

    internal static uint decContextSaveStatus(decContext context, uint mask) => context.status & mask;

    internal static decContext decContextSetRounding(decContext context, rounding mode)
    {
        context.round = mode;
        return context;
    }

    internal static decContext decContextSetStatus(decContext context, uint status)
    {
        context.status |= status;
        if ((status & context.traps) != 0)
        {
            throw new ArithmeticException(decContextStatusToString(context));
        }

        return context;
    }

    internal static decContext? decContextSetStatusFromString(decContext context, string condition)
    {
        var status = StatusFromString(condition);
        return status is null ? null : decContextSetStatus(context, status.Value);
    }

    internal static decContext? decContextSetStatusFromStringQuiet(decContext context, string condition)
    {
        var status = StatusFromString(condition);
        return status is null ? null : decContextSetStatusQuiet(context, status.Value);
    }

    internal static decContext decContextSetStatusQuiet(decContext context, uint status)
    {
        context.status |= status;
        return context;
    }

    internal static string decContextStatusToString(decContext context) => context.status switch
    {
        DEC_Invalid_operation => DEC_Condition_IO,
        DEC_Division_by_zero => DEC_Condition_DZ,
        DEC_Overflow => DEC_Condition_OV,
        DEC_Underflow => DEC_Condition_UN,
        DEC_Inexact => DEC_Condition_IE,
        DEC_Division_impossible => DEC_Condition_DI,
        DEC_Division_undefined => DEC_Condition_DU,
        DEC_Rounded => DEC_Condition_RO,
        DEC_Clamped => DEC_Condition_PA,
        DEC_Subnormal => DEC_Condition_SU,
        DEC_Conversion_syntax => DEC_Condition_CS,
        DEC_Insufficient_storage => DEC_Condition_IS,
        DEC_Invalid_context => DEC_Condition_IC,
        0 => DEC_Condition_ZE,
        _ => DEC_Condition_MU,
    };

    internal static int decContextTestEndian(byte quiet)
    {
        _ = quiet;
        return BitConverter.IsLittleEndian ? 0 : -1;
    }

    internal static uint decContextTestSavedStatus(uint savedStatus, uint mask) =>
        (savedStatus & mask) != 0 ? 1u : 0u;

    internal static uint decContextTestStatus(decContext context, uint mask) =>
        (context.status & mask) != 0 ? 1u : 0u;

    internal static decContext decContextZeroStatus(decContext context)
    {
        context.status = 0;
        return context;
    }

    internal static decContext CreateJqDecimalContext()
    {
        var context = decContextDefault(new decContext(), DEC_INIT_BASE);
        context.digits = Math.Min(
            DEC_MAX_DIGITS,
            int.MaxValue - (DECDPUN - 1) - (context.emax - context.emin - 1));
        context.traps = 0;
        return context;
    }

    private static void SetIeeeContext(decContext context, int digits, int emax, int emin)
    {
        context.digits = digits;
        context.emax = emax;
        context.emin = emin;
        context.round = rounding.DEC_ROUND_HALF_EVEN;
        context.traps = 0;
        context.clamp = 1;
    }

    private static uint? StatusFromString(string condition) => condition switch
    {
        DEC_Condition_CS => DEC_Conversion_syntax,
        DEC_Condition_DZ => DEC_Division_by_zero,
        DEC_Condition_DI => DEC_Division_impossible,
        DEC_Condition_DU => DEC_Division_undefined,
        DEC_Condition_IE => DEC_Inexact,
        DEC_Condition_IS => DEC_Insufficient_storage,
        DEC_Condition_IC => DEC_Invalid_context,
        DEC_Condition_IO => DEC_Invalid_operation,
        DEC_Condition_OV => DEC_Overflow,
        DEC_Condition_PA => DEC_Clamped,
        DEC_Condition_RO => DEC_Rounded,
        DEC_Condition_SU => DEC_Subnormal,
        DEC_Condition_UN => DEC_Underflow,
        DEC_Condition_ZE => 0,
        _ => null,
    };
}

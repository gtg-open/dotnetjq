// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: vendor/decNumber/decNumber.h
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/vendor/decNumber/decNumber.h
// Strategy: PROXY
// Target file: src/DotNetJq/Port/vendor/decNumber/decNumber.h.cs
// Upstream copyright notice: Copyright (c) IBM Corporation, 2000, 2010. All rights reserved.
// Upstream license: ICU License -- ICU 1.8.1 and later; complete terms are in /COPYING.jq.
// UPSTREAM COMPONENT: IBM decNumber value representation and public classification helpers used by jq.
// REPLACEMENT: ManagedDecimal arbitrary-precision coefficient/exponent representation.
// WHY: an immutable managed payload replaces the variable-length native lsu allocation.
// BEHAVIORAL CONTRACT: retain coefficient, exponent, sign, special-value flags, and jq-shaped accessors.
// KNOWN DIFFERENCES: storage is managed and does not expose a writable trailing lsu array.
// TESTS COVERING THE SUBSTITUTION: NumericCompatibilityTests, NumericCompatibilityRound2Tests, and build coverage.

namespace DotNetJq.Port;

internal sealed class decNumber
{
    internal decNumber()
        : this(ManagedDecimal.Zero())
    {
    }

    internal decNumber(ManagedDecimal value)
    {
        Value = value;
    }

    internal ManagedDecimal Value { get; private set; }

    internal int digits => Value.Digits;

    internal int exponent => Value.Exponent;

    internal byte bits =>
        (byte)((Value.IsNegative ? libjq.DECNEG : 0) |
            (Value.Kind switch
            {
                ManagedDecimalKind.Infinity => libjq.DECINF,
                ManagedDecimalKind.QuietNaN => libjq.DECNAN,
                ManagedDecimalKind.SignalingNaN => libjq.DECSNAN,
                _ => 0,
            }));

    internal ushort[] lsu
    {
        get
        {
            var coefficient = Value.Coefficient;
            var units = new ushort[Math.Max(1, (digits + libjq.DECDPUN - 1) / libjq.DECDPUN)];
            for (var i = 0; i < units.Length; i++)
            {
                coefficient = System.Numerics.BigInteger.DivRem(coefficient, 1000, out var unit);
                units[i] = (ushort)unit;
            }

            return units;
        }
    }

    internal decNumber Set(ManagedDecimal value)
    {
        Value = value;
        return this;
    }
}

internal static partial class libjq
{
    internal const byte DECNEG = 0x80;
    internal const byte DECINF = 0x40;
    internal const byte DECNAN = 0x20;
    internal const byte DECSNAN = 0x10;
    internal const byte DECSPECIAL = DECINF | DECNAN | DECSNAN;
    internal const int DECDPUN = 3;
    internal const int DECNUMDIGITS = 1;
    internal const int DECNUMUNITS = 1;

    internal static bool decNumberIsCanonical(decNumber number) => number is not null;

    internal static bool decNumberIsFinite(decNumber number) => number.Value.IsFinite;

    internal static bool decNumberIsInfinite(decNumber number) => number.Value.IsInfinity;

    internal static bool decNumberIsNaN(decNumber number) => number.Value.IsNaN;

    internal static bool decNumberIsNegative(decNumber number) => number.Value.IsNegative;

    internal static bool decNumberIsQNaN(decNumber number) =>
        number.Value.Kind == ManagedDecimalKind.QuietNaN;

    internal static bool decNumberIsSNaN(decNumber number) =>
        number.Value.Kind == ManagedDecimalKind.SignalingNaN;

    internal static bool decNumberIsSpecial(decNumber number) => !number.Value.IsFinite;

    internal static bool decNumberIsZero(decNumber number) => number.Value.IsZero;

    internal static int decNumberRadix(decNumber number)
    {
        _ = number;
        return 10;
    }
}

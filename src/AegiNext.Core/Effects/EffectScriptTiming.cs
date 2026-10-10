using System.Numerics;
using AegiNext.Core.Timing;

namespace AegiNext.Core.Effects;

internal static class EffectScriptTiming
{
    internal static BigInteger ExactFloor(MediaTime length, MediaTime period)
    {
        if (length < MediaTime.Zero || period <= MediaTime.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }
        return (BigInteger)length.Numerator * period.Denominator / ((BigInteger)length.Denominator * period.Numerator);
    }

    internal static MediaTime Scale(MediaTime time, decimal factor)
    {
        var bits = decimal.GetBits(factor);
        var coefficient = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var denominator = BigInteger.Pow(10, (bits[3] >> 16) & 0xff);
        var divisor = BigInteger.GreatestCommonDivisor(coefficient, denominator);
        coefficient /= divisor;
        denominator /= divisor;
        if (bits[3] < 0)
        {
            coefficient = -coefficient;
        }

        return Scale(time, new(checked((long)coefficient), checked((long)denominator)), new(1));
    }

    internal static MediaTime Scale(MediaTime time, MediaTime numerator, MediaTime denominator)
    {
        var top = (BigInteger)time.Numerator * numerator.Numerator * denominator.Denominator;
        var bottom = (BigInteger)time.Denominator * numerator.Denominator * denominator.Numerator;
        var divisor = BigInteger.GreatestCommonDivisor(top, bottom);
        return new(checked((long)(top / divisor)), checked((long)(bottom / divisor)));
    }
}

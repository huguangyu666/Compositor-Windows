// Compositor's CPU float boundaries, hosted by Microsoft's MIT-licensed Win2D.
int ArithmeticBarrier;
float F(float value) { return asfloat(asuint(value) ^ uint(ArithmeticBarrier)); }
float Add(float a, float b) { return F(a + b); }
float Mul(float a, float b) { return F(a * b); }

// IEEE division for finite normal inputs. Shader-model-4 reciprocal division may
// differ from CPU division at a rounding boundary. All callers guard zero divisors.
float Div(float numerator, float denominator)
{
    if (numerator == 0) return 0;
    uint n = asuint(numerator), d = asuint(denominator), sign = (n ^ d) & 0x80000000;
    n &= 0x7fffffff; d &= 0x7fffffff;
    int exponent = int(n >> 23) - int(d >> 23) + 127;
    uint remainder = (n & 0x7fffff) | 0x800000, divisor = (d & 0x7fffff) | 0x800000;
    if (remainder < divisor) { remainder <<= 1; exponent--; }
    remainder -= divisor;
    uint quotient = 0x800000;
    [loop] for (int bit = 22; bit >= 0; bit--)
    {
        remainder <<= 1;
        if (remainder >= divisor) { remainder -= divisor; quotient |= 1u << bit; }
    }
    uint twice = remainder << 1;
    if (twice > divisor || (twice == divisor && (quotient & 1) != 0)) quotient++;
    if (quotient == 0x1000000) { quotient >>= 1; exponent++; }
    return asfloat(sign | (uint(exponent) << 23) | (quotient & 0x7fffff));
}

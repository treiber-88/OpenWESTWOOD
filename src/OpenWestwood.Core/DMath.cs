namespace OpenWestwood.Core;

/// <summary>
/// Trigonometry that gives the very same result on every computer. Math.Sin and friends call the operating system's
/// maths library, which differs in the last bit between Windows, Linux and macOS; in a network game every machine runs
/// the same simulation, and one bit's difference grows until the games no longer match. These use only +, -, *, / and
/// square roots (exact by the IEEE standard on every processor), after Sun's fdlibm.
///
/// Game logic uses these; drawing and sound may use Math.
/// </summary>
public static class DMath
{
    const double PiO2Hi = 1.57079632673412561417e+00, PiO2Lo = 6.07710050650619224932e-11;
    const double TwoOverPi = 6.36619772367581382433e-01;

    public static double Sin(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        double k = Math.Round(x * TwoOverPi);
        double r = x - k * PiO2Hi - k * PiO2Lo;
        return ((long)k & 3) switch
        {
            0 => KernelSin(r),
            1 => KernelCos(r),
            2 => -KernelSin(r),
            _ => -KernelCos(r),
        };
    }

    public static double Cos(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
            return double.NaN;
        double k = Math.Round(x * TwoOverPi);
        double r = x - k * PiO2Hi - k * PiO2Lo;
        return ((long)k & 3) switch
        {
            0 => KernelCos(r),
            1 => -KernelSin(r),
            2 => -KernelCos(r),
            _ => KernelSin(r),
        };
    }

    // fdlibm __kernel_sin / __kernel_cos on [-pi/4, pi/4].
    static double KernelSin(double x)
    {
        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
            S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
        double z = x * x, v = z * x;
        double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
        return x + v * (S1 + z * r);
    }

    static double KernelCos(double x)
    {
        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
            C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;
        double z = x * x;
        double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
        double hz = 0.5 * z, w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + z * r);
    }

    static readonly double[] AtanHi = [4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00];
    static readonly double[] AtanLo = [2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17];

    public static double Atan(double x)
    {
        if (double.IsNaN(x))
            return double.NaN;
        const double A0 = 3.33333333333329318027e-01, A1 = -1.99999999998764832476e-01, A2 = 1.42857142725034663711e-01,
            A3 = -1.11111104054623557880e-01, A4 = 9.09088713343650656196e-02, A5 = -7.69187620504482999495e-02,
            A6 = 6.66107313738753120669e-02, A7 = -5.83357013379057348645e-02, A8 = 4.97687799461593236017e-02,
            A9 = -3.65315727442169155270e-02, A10 = 1.62858201153657823623e-02;
        bool negative = x < 0;
        double ax = Math.Abs(x);
        if (ax >= 4.4028236692093846346e+19)
            return negative ? -(AtanHi[3] + AtanLo[3]) : AtanHi[3] + AtanLo[3];
        int id;
        if (ax < 0.4375)
        {
            if (ax < 1e-29)
                return x;
            id = -1;
        }
        else if (ax < 1.1875)
        {
            if (ax < 0.6875) { id = 0; ax = (2.0 * ax - 1.0) / (2.0 + ax); }
            else { id = 1; ax = (ax - 1.0) / (ax + 1.0); }
        }
        else if (ax < 2.4375) { id = 2; ax = (ax - 1.5) / (1.0 + 1.5 * ax); }
        else { id = 3; ax = -1.0 / ax; }
        double z = ax * ax, w = z * z;
        double s1 = z * (A0 + w * (A2 + w * (A4 + w * (A6 + w * (A8 + w * A10)))));
        double s2 = w * (A1 + w * (A3 + w * (A5 + w * (A7 + w * A9))));
        double result = id < 0 ? ax - ax * (s1 + s2) : AtanHi[id] - ((ax * (s1 + s2) - AtanLo[id]) - ax);
        return negative ? -result : result;
    }

    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return double.NaN;
        if (x == 0)
        {
            if (y == 0)
                return double.IsNegative(x) ? (double.IsNegative(y) ? -Math.PI : Math.PI) : (double.IsNegative(y) ? -0.0 : 0.0);
            return y > 0 ? Math.PI / 2 : -Math.PI / 2;
        }
        if (double.IsInfinity(x) || double.IsInfinity(y))
        {
            if (double.IsInfinity(x) && double.IsInfinity(y))
                return (x > 0 ? Math.PI / 4 : 3 * Math.PI / 4) * Math.Sign(y);
            if (double.IsInfinity(y))
                return y > 0 ? Math.PI / 2 : -Math.PI / 2;
            return x > 0 ? (double.IsNegative(y) ? -0.0 : 0.0) : (double.IsNegative(y) ? -Math.PI : Math.PI);
        }
        double a = Atan(Math.Abs(y / x));
        if (x > 0)
            return double.IsNegative(y) ? -a : a;
        return double.IsNegative(y) ? -(Math.PI - a) : Math.PI - a;
    }

    public static double Asin(double x) => x >= 1 ? Math.PI / 2 : x <= -1 ? -Math.PI / 2 : Atan2(x, Math.Sqrt((1 - x) * (1 + x)));

    public static double Acos(double x) => x >= 1 ? 0 : x <= -1 ? Math.PI : Atan2(Math.Sqrt((1 - x) * (1 + x)), x);

    // ---- The rotations System.Numerics builds with MathF.Sin/Cos (the same matrices, from the functions above) ----

    public static System.Numerics.Matrix4x4 RotationX(float radians)
    {
        float c = Cos(radians), s = Sin(radians);
        var m = System.Numerics.Matrix4x4.Identity;
        m.M22 = c; m.M23 = s;
        m.M32 = -s; m.M33 = c;
        return m;
    }

    public static System.Numerics.Matrix4x4 RotationY(float radians)
    {
        float c = Cos(radians), s = Sin(radians);
        var m = System.Numerics.Matrix4x4.Identity;
        m.M11 = c; m.M13 = -s;
        m.M31 = s; m.M33 = c;
        return m;
    }

    public static System.Numerics.Matrix4x4 RotationZ(float radians)
    {
        float c = Cos(radians), s = Sin(radians);
        var m = System.Numerics.Matrix4x4.Identity;
        m.M11 = c; m.M12 = s;
        m.M21 = -s; m.M22 = c;
        return m;
    }

    public static System.Numerics.Quaternion QuaternionFromAxisAngle(System.Numerics.Vector3 axis, float angle)
    {
        float half = angle * 0.5f;
        float s = Sin(half), c = Cos(half);
        return new System.Numerics.Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    public static float Sin(float x) => (float)Sin((double)x);
    public static float Cos(float x) => (float)Cos((double)x);
    public static float Atan(float x) => (float)Atan((double)x);
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);
    public static float Asin(float x) => (float)Asin((double)x);
    public static float Acos(float x) => (float)Acos((double)x);
}

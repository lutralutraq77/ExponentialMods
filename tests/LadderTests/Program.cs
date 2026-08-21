using System;
using System.Collections.Generic;
using ExponentialMods;

internal static class Program
{
    private static int Fail;
    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) Fail++;
    }

    private static List<int> Climb(int b, int maxExp, int ceiling, int picks)
    {
        var seq = new List<int> { 0 };
        int cur = 0;
        for (int i = 0; i < picks; i++) { cur = ExponentialLadder.GetNextRung(cur, b, maxExp, ceiling); seq.Add(cur); }
        return seq;
    }

    private static void Main()
    {
        int SAFE = ExponentialLadder.SafeStackDefault;   // 16,777,216
        int HARD = ExponentialLadder.RoR2StackHardLimit; // 2,147,483,647

        Console.WriteLine($"RoR2 hard stack limit : {HARD:N0}  (int.MaxValue -- Inventory clamps here)");
        Console.WriteLine($"Recommended ceiling   : {SAFE:N0}  (2^24)");
        Console.WriteLine();

        Console.WriteLine("== Ladders from an empty stack ==");
        foreach (int b in new[] { 2, 3, 5, 10 })
            Console.WriteLine($"  base {b,-3} -> {string.Join(", ", Climb(b, 0, SAFE, 12))}");
        Console.WriteLine();

        Console.WriteLine("== Auto max exponent per base (ceiling 2^24) ==");
        foreach (int b in new[] { 2, 3, 4, 5, 10, 64 })
        {
            int n = ExponentialLadder.MaxSafeExponent(b, SAFE);
            int top = ExponentialLadder.Pow(b, n, SAFE);
            long over = (long)top * b;
            Console.WriteLine($"  base {b,-3} n={n,-3} top={top,12:N0}   next rung would be {over:N0}");
            Check(top <= SAFE && over > SAFE, $"base {b}: base^{n} fits in the ceiling and base^{n + 1} does not");
        }
        Console.WriteLine();

        Console.WriteLine("== Exact power ladder ==");
        foreach (int b in new[] { 2, 3, 10 })
        {
            var seq = Climb(b, 0, SAFE, 6);
            bool ok = seq[0] == 0;
            for (int i = 1; i < seq.Count; i++) ok &= seq[i] == (int)Math.Pow(b, i - 1);
            Check(ok, $"base {b}: pickups land exactly on {b}^0, {b}^1, {b}^2, ...");
        }
        Console.WriteLine();

        Console.WriteLine("== Regression: monotonic, never exceeds ceiling, never overflows ==");
        foreach (int b in new[] { 2, 3, 7, 64 })
        foreach (int ceiling in new[] { 1, 2, 1000, SAFE, HARD })
        {
            // The ladder's summit is base^maxSafeExponent, which legitimately sits BELOW the
            // ceiling whenever the ceiling is not itself a power of base (e.g. base 3 with a
            // ceiling of 1000 tops out at 3^6 = 729). Climbing must be strict up to that
            // summit and must hold there.
            int summit = ExponentialLadder.Pow(b, ExponentialLadder.MaxSafeExponent(b, ceiling), ceiling);
            bool mono = true, bounded = true;
            int cur = 0;
            for (int i = 0; i < 64; i++)
            {
                int next = ExponentialLadder.GetNextRung(cur, b, 0, ceiling);
                if (next < cur) mono = false;
                if (next > ceiling || next < 1) bounded = false;
                if (next == cur && cur < summit) mono = false;   // stalled before the summit
                cur = next;
            }
            Check(mono, $"base {b} ceiling {ceiling:N0}: strictly climbs to the {summit:N0} summit, then holds");
            Check(bounded, $"base {b} ceiling {ceiling:N0}: stays inside [1, ceiling]");
            Check(cur == summit, $"base {b} ceiling {ceiling:N0}: settles exactly on {summit:N0} = {b}^{ExponentialLadder.MaxSafeExponent(b, ceiling)}");
        }
        Console.WriteLine();

        Console.WriteLine("== Regression: hostile inputs ==");
        Check(ExponentialLadder.GetNextRung(int.MaxValue, 2, 0, HARD) == int.MaxValue, "stack already at int.MaxValue holds, no wrap");
        Check(ExponentialLadder.GetNextRung(-5, 2, 0, SAFE) == 1, "negative current stack recovers to rung 1");
        Check(ExponentialLadder.GetNextRung(0, 1, 0, SAFE) == 1 && ExponentialLadder.ClampBase(1) == 2, "base below 2 is clamped to 2");
        Check(ExponentialLadder.ClampBase(9999) == ExponentialLadder.MaxBase, "oversized base is clamped");
        Check(ExponentialLadder.Pow(64, 30, HARD) == HARD, "Pow saturates at the ceiling instead of wrapping negative");
        Check(ExponentialLadder.GetNextRung(100, 2, 3, SAFE) == 100, "a stack above the max-exponent top rung is held, not reduced");
        Check(ExponentialLadder.GetNextRung(5, 2, 3, SAFE) == 8, "max exponent 3 caps the ladder at 2^3 = 8");
        Check(ExponentialLadder.GetNextRung(8, 2, 3, SAFE) == 8, "at the max-exponent top rung the ladder holds");
        Console.WriteLine();

        Console.WriteLine("== Every stack value a real run can reach maps to a valid rung (base 2) ==");
        bool allOk = true;
        for (int c = 0; c < 200000; c++)
        {
            int n = ExponentialLadder.GetNextRung(c, 2, 0, SAFE);
            if (n <= c || n > SAFE) { allOk = false; Console.WriteLine($"    broke at {c} -> {n}"); break; }
        }
        Check(allOk, "base 2: every stack 0..199,999 advances to a larger valid rung");

        Console.WriteLine();
        Console.WriteLine(Fail == 0 ? "ALL CHECKS PASSED" : $"{Fail} CHECK(S) FAILED");
        Environment.Exit(Fail == 0 ? 0 : 1);
    }
}

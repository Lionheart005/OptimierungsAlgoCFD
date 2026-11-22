using System;
using System.Numerics;

namespace FluidForge.Core
{
    /// <summary>
    /// Low-discrepancy Sobol quasi-random sequence with Joe &amp; Kuo (2008) direction numbers
    /// for dimensions 1..64 and a prime-based Halton fallback above 64. Pure C#, zero-dependency.
    /// </summary>
    public static class SobolSequence
    {
        private const int Bits = 32;
        private const double Scale = 1.0 / 4294967296.0; // 2^-32
        private const double ClampEps = 1e-6;

        private readonly record struct DirectionSpec(int S, uint A, int[] M);

        // Joe & Kuo (2008) new-joe-kuo-6.21201 direction numbers for dimensions d = 2..64
        // (index 0 corresponds to dimension d = 2).
        private static readonly DirectionSpec[] Specs =
        {
            new(1, 0, new[] { 1 }),                                              // d = 2
            new(2, 1, new[] { 1, 3 }),                                           // d = 3
            new(3, 1, new[] { 1, 3, 1 }),                                        // d = 4
            new(3, 2, new[] { 1, 1, 1 }),                                        // d = 5
            new(4, 1, new[] { 1, 1, 3, 3 }),                                     // d = 6
            new(4, 4, new[] { 1, 3, 5, 13 }),                                    // d = 7
            new(5, 2, new[] { 1, 1, 5, 5, 17 }),                                 // d = 8
            new(5, 4, new[] { 1, 1, 5, 5, 5 }),                                  // d = 9
            new(5, 7, new[] { 1, 1, 7, 11, 19 }),                                // d = 10
            new(5, 11, new[] { 1, 1, 5, 1, 1 }),                                 // d = 11
            new(5, 13, new[] { 1, 1, 1, 3, 11 }),                                // d = 12
            new(5, 14, new[] { 1, 3, 5, 5, 31 }),                                // d = 13
            new(6, 1, new[] { 1, 3, 3, 9, 7, 49 }),                              // d = 14
            new(6, 13, new[] { 1, 1, 1, 15, 21, 21 }),                           // d = 15
            new(6, 16, new[] { 1, 3, 1, 13, 27, 49 }),                           // d = 16
            new(6, 19, new[] { 1, 1, 1, 15, 7, 5 }),                             // d = 17
            new(6, 22, new[] { 1, 3, 1, 15, 13, 25 }),                           // d = 18
            new(6, 25, new[] { 1, 1, 5, 5, 19, 61 }),                            // d = 19
            new(7, 1, new[] { 1, 3, 7, 11, 23, 15, 103 }),                       // d = 20
            new(7, 4, new[] { 1, 3, 7, 13, 13, 15, 69 }),                        // d = 21
            new(7, 7, new[] { 1, 1, 3, 13, 7, 35, 63 }),                         // d = 22
            new(7, 8, new[] { 1, 3, 5, 9, 1, 25, 53 }),                          // d = 23
            new(7, 14, new[] { 1, 3, 1, 13, 9, 35, 107 }),                       // d = 24
            new(7, 19, new[] { 1, 3, 1, 5, 27, 61, 31 }),                        // d = 25
            new(7, 21, new[] { 1, 1, 5, 11, 19, 41, 61 }),                       // d = 26
            new(7, 28, new[] { 1, 3, 5, 3, 3, 13, 69 }),                         // d = 27
            new(7, 31, new[] { 1, 1, 7, 13, 1, 19, 1 }),                         // d = 28
            new(7, 32, new[] { 1, 3, 7, 5, 13, 19, 59 }),                        // d = 29
            new(7, 37, new[] { 1, 1, 3, 9, 25, 29, 41 }),                        // d = 30
            new(7, 41, new[] { 1, 3, 5, 13, 23, 1, 55 }),                        // d = 31
            new(7, 42, new[] { 1, 3, 7, 3, 13, 59, 17 }),                        // d = 32
            new(7, 50, new[] { 1, 1, 3, 5, 23, 35, 25 }),                        // d = 33
            new(7, 55, new[] { 1, 1, 3, 13, 11, 27, 29 }),                       // d = 34
            new(7, 56, new[] { 1, 3, 7, 7, 13, 25, 15 }),                        // d = 35
            new(7, 59, new[] { 1, 1, 1, 11, 19, 31, 75 }),                       // d = 36
            new(7, 62, new[] { 1, 3, 1, 7, 21, 61, 121 }),                       // d = 37
            new(8, 14, new[] { 1, 3, 5, 5, 21, 41, 125, 191 }),                  // d = 38
            new(8, 21, new[] { 1, 1, 1, 9, 23, 37, 23, 11 }),                    // d = 39
            new(8, 22, new[] { 1, 3, 7, 7, 19, 47, 75, 241 }),                   // d = 40
            new(8, 38, new[] { 1, 1, 5, 1, 21, 5, 17, 123 }),                    // d = 41
            new(8, 47, new[] { 1, 3, 5, 7, 25, 31, 47, 159 }),                   // d = 42
            new(8, 49, new[] { 1, 1, 1, 15, 29, 15, 85, 81 }),                   // d = 43
            new(8, 50, new[] { 1, 3, 3, 5, 19, 25, 117, 125 }),                  // d = 44
            new(8, 52, new[] { 1, 3, 7, 15, 13, 27, 45, 71 }),                   // d = 45
            new(8, 56, new[] { 1, 1, 1, 3, 17, 19, 31, 251 }),                   // d = 46
            new(8, 67, new[] { 1, 3, 7, 9, 19, 47, 41, 165 }),                   // d = 47
            new(8, 70, new[] { 1, 1, 5, 13, 19, 45, 39, 187 }),                  // d = 48
            new(8, 84, new[] { 1, 1, 7, 15, 17, 57, 71, 243 }),                  // d = 49
            new(8, 97, new[] { 1, 3, 5, 1, 1, 19, 119, 219 }),                   // d = 50
            new(8, 103, new[] { 1, 1, 5, 3, 15, 53, 81, 15 }),                   // d = 51
            new(8, 115, new[] { 1, 3, 7, 3, 19, 37, 15, 79 }),                   // d = 52
            new(8, 122, new[] { 1, 3, 1, 7, 25, 61, 89, 193 }),                  // d = 53
            new(9, 8, new[] { 1, 1, 1, 11, 25, 15, 59, 63, 209 }),               // d = 54
            new(9, 13, new[] { 1, 1, 7, 9, 25, 5, 127, 135, 399 }),              // d = 55
            new(9, 16, new[] { 1, 3, 5, 9, 1, 47, 39, 99, 23 }),                 // d = 56
            new(9, 22, new[] { 1, 3, 7, 7, 11, 15, 75, 217, 443 }),              // d = 57
            new(9, 25, new[] { 1, 1, 3, 1, 27, 25, 55, 105, 421 }),              // d = 58
            new(9, 44, new[] { 1, 1, 1, 13, 21, 5, 79, 57, 463 }),               // d = 59
            new(9, 47, new[] { 1, 3, 7, 5, 19, 61, 113, 189, 161 }),             // d = 60
            new(9, 52, new[] { 1, 1, 7, 5, 1, 21, 95, 223, 59 }),                // d = 61
            new(9, 55, new[] { 1, 3, 5, 11, 15, 35, 123, 35, 33 }),              // d = 62
            new(9, 59, new[] { 1, 3, 3, 15, 29, 21, 89, 77, 189 }),              // d = 63
            new(9, 62, new[] { 1, 3, 5, 1, 23, 53, 87, 81, 365 })                // d = 64
        };

        private static readonly int MaxSobolDimensions = 1 + Specs.Length; // 64
        private static readonly uint[][] DirectionNumbers = BuildDirectionNumbers();

        private static uint[][] BuildDirectionNumbers()
        {
            var table = new uint[MaxSobolDimensions][];

            // Dimension 0 (d = 1): v_j = 2^(31 - j)
            var d0 = new uint[Bits];
            for (int j = 0; j < Bits; j++)
            {
                d0[j] = 1u << (Bits - 1 - j);
            }
            table[0] = d0;

            for (int idx = 0; idx < Specs.Length; idx++)
            {
                DirectionSpec spec = Specs[idx];
                var v = new uint[Bits];
                int s = spec.S;
                uint a = spec.A;

                for (int j = 0; j < s; j++)
                {
                    v[j] = (uint)spec.M[j] << (Bits - 1 - j);
                }

                for (int j = s; j < Bits; j++)
                {
                    uint val = v[j - s] ^ (v[j - s] >> s);
                    for (int k = 1; k < s; k++)
                    {
                        if (((a >> (s - 1 - k)) & 1u) != 0u)
                        {
                            val ^= v[j - k];
                        }
                    }
                    v[j] = val;
                }

                table[idx + 1] = v;
            }

            return table;
        }

        /// <summary>
        /// Generates <paramref name="count"/> deterministic Sobol points in (0, 1)^<paramref name="dimension"/>.
        /// Skips the origin by default (<paramref name="skip"/> = 1). Falls back to prime-based Halton
        /// radical inverses beyond dimension 64.
        /// </summary>
        public static double[][] Generate(int count, int dimension, int skip = 1)
        {
            if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip), "skip must be non-negative.");
            if (count <= 0 || dimension <= 0) return Array.Empty<double[]>();

            var result = new double[count][];
            for (int i = 0; i < count; i++)
            {
                result[i] = new double[dimension];
            }

            int sobolDims = Math.Min(dimension, MaxSobolDimensions);
            var state = new uint[sobolDims];

            // Advance directly to index `skip` using Gray code g(skip) = skip ^ (skip >> 1).
            if (skip > 0)
            {
                uint gray = (uint)skip ^ ((uint)skip >> 1);
                for (int bit = 0; gray != 0u && bit < Bits; bit++, gray >>= 1)
                {
                    if ((gray & 1u) != 0u)
                    {
                        for (int dim = 0; dim < sobolDims; dim++)
                        {
                            state[dim] ^= DirectionNumbers[dim][bit];
                        }
                    }
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    uint index = (uint)(skip + i);
                    int bit = BitOperations.TrailingZeroCount(index);
                    if (bit < Bits)
                    {
                        for (int dim = 0; dim < sobolDims; dim++)
                        {
                            state[dim] ^= DirectionNumbers[dim][bit];
                        }
                    }
                }

                double[] row = result[i];
                for (int dim = 0; dim < sobolDims; dim++)
                {
                    row[dim] = state[dim] * Scale;
                }
            }

            // Fallback to prime-based Halton sequence for any dimensions above 64.
            if (dimension > MaxSobolDimensions)
            {
                int extraDims = dimension - MaxSobolDimensions;
                int[] primes = GetPrimesStartingAfter64(extraDims);
                for (int i = 0; i < count; i++)
                {
                    int haltonIdx = skip + i;
                    double[] row = result[i];
                    for (int k = 0; k < extraDims; k++)
                    {
                        row[MaxSobolDimensions + k] = RadicalInverse(haltonIdx, primes[k]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Generates Cranley-Patterson randomly shifted Sobol points in [1e-6, 1 - 1e-6]^<paramref name="dimension"/>,
        /// deterministic for a given <paramref name="random"/> seed.
        /// </summary>
        public static double[][] GenerateShifted(int count, int dimension, Random random, int skip = 1)
        {
            ArgumentNullException.ThrowIfNull(random);
            if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip), "skip must be non-negative.");
            if (count <= 0 || dimension <= 0) return Array.Empty<double[]>();

            double[][] points = Generate(count, dimension, skip);
            var shifts = new double[dimension];
            for (int d = 0; d < dimension; d++)
            {
                shifts[d] = random.NextDouble();
            }

            for (int i = 0; i < count; i++)
            {
                double[] row = points[i];
                for (int d = 0; d < dimension; d++)
                {
                    double val = row[d] + shifts[d];
                    if (val >= 1.0) val -= 1.0;
                    row[d] = Math.Clamp(val, ClampEps, 1.0 - ClampEps);
                }
            }

            return points;
        }

        /// <summary>
        /// Generates standard normal QMC samples N(0, 1)^<paramref name="dimension"/> by applying
        /// <see cref="NormalDistribution.InverseCdf"/> to Cranley-Patterson shifted Sobol points.
        /// </summary>
        public static double[][] GenerateNormalSamples(int count, int dimension, Random random, int skip = 1)
        {
            double[][] uniform = GenerateShifted(count, dimension, random, skip);
            for (int i = 0; i < uniform.Length; i++)
            {
                double[] row = uniform[i];
                for (int d = 0; d < row.Length; d++)
                {
                    row[d] = NormalDistribution.InverseCdf(row[d]);
                }
            }
            return uniform;
        }

        private static double RadicalInverse(int index, int primeBase)
        {
            if (index <= 0) return 0.5 / primeBase;

            double result = 0.0;
            double f = 1.0 / primeBase;
            int n = index;
            while (n > 0)
            {
                int rem = n % primeBase;
                result += rem * f;
                n /= primeBase;
                f /= primeBase;
            }
            return result;
        }

        private static int[] GetPrimesStartingAfter64(int needed)
        {
            // 65th prime is 313; scan primes cleanly from 313 upward.
            var primes = new int[needed];
            int count = 0;
            int candidate = 313;
            while (count < needed)
            {
                if (IsPrime(candidate))
                {
                    primes[count++] = candidate;
                }
                candidate += 2;
            }
            return primes;
        }

        private static bool IsPrime(int n)
        {
            if (n < 2) return false;
            if ((n & 1) == 0) return n == 2;
            int limit = (int)Math.Sqrt(n);
            for (int d = 3; d <= limit; d += 2)
            {
                if (n % d == 0) return false;
            }
            return true;
        }
    }
}

namespace CilicaLab.Compiler;

public sealed record Sample(string Id, string Name, string Source);

public static class Samples
{
    public static IReadOnlyList<Sample> All { get; } = [
        new("algebra", "Algebra & inlining", """
            public static class Algebra
            {
                public static int Scale(int value) => value * 3;

                public static int Combine(int value)
                {
                    int first = Scale(value);
                    int second = value * 2;
                    return first + second + (value - value);
                }

                public static int Constant() => Combine(7);

                public static int Clamp(int value)
                {
                    if (value < 0) return 0;
                    if (value < 0) return -1;
                    return value;
                }
            }
            """),
        new("vectors", "Loop vectorization", """
            public static class Loops
            {
                public static void Map(int[] source, int[] destination)
                {
                    for (int i = 0; i < source.Length; i++)
                    {
                        destination[i] = source[i] * 3 + 7;
                    }
                }

                public static int Sum(int[] values)
                {
                    int sum = 0;
                    for (int i = 0; i < values.Length; i++)
                    {
                        sum += values[i];
                    }
                    return sum;
                }
            }
            """),
        new("linq", "LINQ expansion", """
            using System.Linq;

            public static class Queries
            {
                public static int CountPositive(int[] values)
                    => values.Where(value => value > 0).Count();

                public static int[] Transform(int[] values)
                    => values.Select(value => value * 3 + 1).ToArray();
            }
            """),
        new("objects", "Scalar replacement", """
            public sealed class Pair
            {
                public int X;
                public int Y;

                public Pair(int x, int y)
                {
                    X = x;
                    Y = y;
                }

                public int Sum() => X + Y;
            }

            public static class Objects
            {
                public static int Calculate(int value)
                {
                    var pair = new Pair(value, 10);
                    return pair.Sum();
                }
            }
            """),
        new("loads", "Repeated loads", """
            public static class Loads
            {
                public static int SumTwice(int[] values, int index)
                    => values[index] + values[index];

                public static int SumTwice(int[] values)
                    => values[0] + values[0];

                public static int WithStore(int[] values, int index)
                {
                    int before = values[index];
                    values[index] = 42;
                    return before + values[index];
                }
            }
            """)
    ];
}

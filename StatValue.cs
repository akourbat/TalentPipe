using System;
using System.Numerics;

namespace SpellTalentSystem;

/// <summary>
/// Generic numeric accumulator separating Base, additive Flat bonuses (+100, +4m, +2 targets),
/// and additive Percentage modifiers (+0.10 -> 1.10x).
/// Keeps Base/Flat in native type T (e.g., int for MaxTargets, double for Effect/Range)
/// while accumulating percentage modifiers in double precision.
/// </summary>
public readonly record struct StatValue<T>(T Base, T Flat, double Multiplier = 1.0)
    where T : struct, INumber<T>
{
    public StatValue(T baseValue) : this(baseValue, T.Zero, 1.0) { }

    public static implicit operator StatValue<T>(T baseValue) => new(baseValue);

    public StatValue<T> AddFlat(T amount) => this with { Flat = Flat + amount };
    public StatValue<T> AddPercent(double percent) => this with { Multiplier = Multiplier + percent };

    public double EvaluateDouble()
    {
        double sum = double.CreateChecked(Base + Flat);
        return Math.Max(0.0, sum * Multiplier);
    }

    public T Evaluate()
    {
        double result = EvaluateDouble();
        return T.IsInteger(T.Zero)
            ? T.CreateChecked(Math.Round(result, MidpointRounding.AwayFromZero))
            : T.CreateChecked(result);
    }

    public override string ToString() =>
        Flat == T.Zero && Math.Abs(Multiplier - 1.0) < 1e-9
            ? $"{Base}"
            : $"({Base} + {Flat}) x {Multiplier:0.##} = {Evaluate()}";
}

public static class StatValueExtensions
{
    public static TimeSpan EvaluateTimeSpan<T>(this StatValue<T> seconds)
        where T : struct, INumber<T> =>
        TimeSpan.FromSeconds(seconds.EvaluateDouble());
}

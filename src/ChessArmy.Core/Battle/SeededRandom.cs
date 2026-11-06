namespace ChessArmy.Core.Battle;

/// <summary>
/// Aléa à graine qui donne EXACTEMENT la même suite que <c>new System.Random(seed)</c> (algorithme historique de
/// .NET, soustractif de Knuth), mais qu'on peut COPIER : la recherche des bots duplique une partie multi en cours
/// (<see cref="Match.Clone"/>) sans la rejouer depuis le début, aléa compris. Un test vérifie l'égalité des suites.
/// </summary>
public sealed class SeededRandom : System.Random
{
    private readonly int[] _seedArray = new int[56];
    private int _inext;
    private int _inextp;

    public SeededRandom(int seed)
    {
        var subtraction = seed == int.MinValue ? int.MaxValue : System.Math.Abs(seed);
        var mj = 161803398 - subtraction;
        _seedArray[55] = mj;
        var mk = 1;
        var ii = 0;
        for (var i = 1; i < 55; i++)
        {
            if ((ii += 21) >= 55) ii -= 55;
            _seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0) mk += int.MaxValue;
            mj = _seedArray[ii];
        }
        for (var k = 1; k < 5; k++)
            for (var i = 1; i < 56; i++)
            {
                var n = i + 30;
                if (n >= 55) n -= 55;
                _seedArray[i] -= _seedArray[1 + n];
                if (_seedArray[i] < 0) _seedArray[i] += int.MaxValue;
            }
        _inext = 0;
        _inextp = 21;
    }

    private SeededRandom(SeededRandom source)
    {
        System.Array.Copy(source._seedArray, _seedArray, _seedArray.Length);
        _inext = source._inext;
        _inextp = source._inextp;
    }

    /// <summary>Copie à l'identique : la copie et l'original tirent ensuite la même suite.</summary>
    public SeededRandom Clone() => new(this);

    private int InternalSample()
    {
        var locINext = _inext;
        if (++locINext >= 56) locINext = 1;
        var locINextp = _inextp;
        if (++locINextp >= 56) locINextp = 1;
        var retVal = _seedArray[locINext] - _seedArray[locINextp];
        if (retVal == int.MaxValue) retVal--;
        if (retVal < 0) retVal += int.MaxValue;
        _seedArray[locINext] = retVal;
        _inext = locINext;
        _inextp = locINextp;
        return retVal;
    }

    protected override double Sample() => InternalSample() * (1.0 / int.MaxValue);

    public override int Next() => InternalSample();

    public override int Next(int maxValue)
    {
        if (maxValue < 0)
            throw new System.ArgumentOutOfRangeException(nameof(maxValue));
        return (int)(Sample() * maxValue);
    }

    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue)
            throw new System.ArgumentOutOfRangeException(nameof(minValue));
        var range = (long)maxValue - minValue;
        return range <= int.MaxValue
            ? (int)(Sample() * range) + minValue
            : (int)((long)(LargeRangeSample() * range) + minValue);
    }

    public override double NextDouble() => Sample();

    private double LargeRangeSample()
    {
        var result = InternalSample();
        if (InternalSample() % 2 == 0)
            result = -result;
        double d = result;
        d += int.MaxValue - 1;
        d /= 2.0 * int.MaxValue - 1;
        return d;
    }
}

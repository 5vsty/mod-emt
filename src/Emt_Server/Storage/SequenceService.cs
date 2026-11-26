namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// Hands out the error numbers (BR-03, D-07: prefix + at least MinDigits digits). A number is only consumed after the error
/// was written successfully, so a failed write does not leave a gap. Callers must hold the creation lock of the ErrorService.
/// </summary>
public class SequenceService
{
    private readonly string Prefix;
    private readonly int MinDigits;
    private long Current;

    public SequenceService(string prefix, int minDigits)
    {
        Prefix = prefix;
        MinDigits = minDigits;
    }

    /// <summary>
    /// Startup: the counter is the maximum of the persisted counter and the highest number found in the errors,
    /// so a number is never given out twice (also if the counter on ILP is behind).
    /// </summary>
    public void Initialize(long persistedValue, long highestNumberSeen) => Current = Math.Max(persistedValue, highestNumberSeen);

    public long Value => Current;

    public long PeekNext() => Current + 1;

    public void Commit(long number) => Current = Math.Max(Current, number);

    public string Format(long number) => Prefix + number.ToString().PadLeft(MinDigits, '0');
}

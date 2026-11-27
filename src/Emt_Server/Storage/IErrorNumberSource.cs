using FSDEV.MOD.Emt.Emt_Server.Catalog;
using System.Diagnostics.CodeAnalysis;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>Where the numbers of new errors come from. Callers must hold the creation lock of the ErrorService.</summary>
public interface IErrorNumberSource
{
    /// <summary>The number the next error gets (numeric, for sorting) and its text. It is only consumed with <see cref="Commit"/>, after the error was written.</summary>
    bool TryReserve(out long number, [NotNullWhen(true)] out string? text);

    void Commit(long number);
}

/// <summary>
/// The numbers of the errors from a series of their own in the IdManager (prefix <see cref="PREFIX"/>, e.g. ER10000001), so a number is unique over
/// the whole system and EMT does not count itself. The IdManager gives the number away when it is asked, so a failed write leaves a gap; that is
/// accepted, the numbers are only identifiers. Errors of an earlier version keep their old numbers (EMT-000008): they sort before the new ones.
/// </summary>
public class IdManagerErrorNumberSource : IErrorNumberSource
{
    public const string PREFIX = "ER";

    private readonly IErrorCodeGenerator Series;
    private readonly string Prefix;

    public IdManagerErrorNumberSource(IErrorCodeGenerator series, string prefix = PREFIX)
    {
        Series = series;
        Prefix = prefix;
    }

    public bool TryReserve(out long number, [NotNullWhen(true)] out string? text)
    {
        number = 0;
        if (!Series.TryGenerate(out text))
        {
            text = null;
            return false;
        }

        if (text.StartsWith(Prefix, StringComparison.Ordinal) && long.TryParse(text.AsSpan(Prefix.Length), out number))
        {
            return true;
        }

        text = null;
        return false;
    }

    public void Commit(long number)
    {
        // nothing to remember: the IdManager counts
    }
}

using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// A number on ILP does not keep its type: a long field with a small value (UsedAt = 0, FinalizedAt = 0) comes back as an IntState after the
/// states went through the server and its database (and a state that the configuration manager creates from a file has the type of its value
/// in the file). The strict readers of ILP then say "not there" and the whole entry was skipped silently. These accept both types.
/// </summary>
public static class IlpNumberReading
{
    public static bool TryGetLongTolerant(this DictionaryState<StringState, IBaseState> dict, string key, out long value)
    {
        value = 0;
        if (!dict.TryGetValue(new StringState(key), out var state))
        {
            return false;
        }

        switch (state)
        {
            case LongState longState:
                value = longState.Value;
                return true;
            case IntState intState:
                value = intState.Value;
                return true;
            default:
                return false;
        }
    }

    public static bool TryGetIntTolerant(this DictionaryState<StringState, IBaseState> dict, string key, out int value)
    {
        value = 0;
        if (!dict.TryGetLongTolerant(key, out var number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }

        value = (int)number;
        return true;
    }
}

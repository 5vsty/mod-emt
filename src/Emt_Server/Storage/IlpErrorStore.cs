using FSDEV.Common.Logger;
using FSDEV.ILP.Intralogistikplattform.SystemState;
using FSDEV.ILP.Intralogistikplattform.SystemState.Client;
using FSDEV.ILP.Intralogistikplattform.SystemState.Messages;
using FSDEV.ILP.Intralogistikplattform.SystemState.States;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.CollectionStates;
using FSDEV.ILP.Intralogistikplattform.SystemState.States.PrimitiveStates;
using FSDEV.MOD.Emt.Emt.Domain;
using Constants = FSDEV.MOD.Emt.Emt.Constants;

namespace FSDEV.MOD.Emt.Emt_Server.Storage;

/// <summary>
/// Stores every error as its own small dictionary at Emt/Errors/&lt;yyyyMM&gt;/&lt;ErrorId&gt;. EMT is the only writer.
/// All changes of one operation are sent in a single ChangeRequest; the ILP server validates all changes of a request
/// on an overlay and rejects the whole request if one change is invalid (see ServerController.HandleChangeRequest).
/// </summary>
public class IlpErrorStore : IErrorStore
{
    private const string LOG_PREFIX = $"[{nameof(IlpErrorStore)}]";

    private readonly IClientController Ilp;

    public IlpErrorStore(IClientController ilp)
    {
        Ilp = ilp;
    }

    public bool TryEnsureRoots()
    {
        var changeRequest = new ChangeRequest(Ilp);

        foreach (var path in new[] { Constants.Ilp.Paths.ROOT, Constants.Ilp.Paths.ERRORS, Constants.Ilp.Paths.SEQUENCE })
        {
            if (!Ilp.TryGetState(path, out _))
            {
                changeRequest.CreateState(new DictionaryState<StringState, IBaseState>(path));
            }
        }

        if (!Ilp.TryGetState(Constants.Ilp.Paths.ERROR_NUMBER, out _))
        {
            changeRequest.CreateState(new LongState(Constants.Ilp.Paths.ERROR_NUMBER, 0));
        }

        return Send(changeRequest, "create base states");
    }

    public bool TryLoadAll(out List<ErrorRecord> records)
    {
        records = [];

        if (!Ilp.TryGetState(Constants.Ilp.Paths.ERRORS, out var errorsState))
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX}[{nameof(TryLoadAll)}] State not found on ilp: {Constants.Ilp.Paths.ERRORS}");
            return false;
        }

        if (errorsState is not DictionaryState<StringState, IBaseState> months)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX}[{nameof(TryLoadAll)}] State is not a dictState: {Constants.Ilp.Paths.ERRORS}");
            return false;
        }

        foreach (var monthState in months.Values)
        {
            if (monthState is not DictionaryState<StringState, IBaseState> month)
            {
                Logging.Log(LogLevel.Warning, $"{LOG_PREFIX}[{nameof(TryLoadAll)}] Skip {monthState.Key}, not a dictState");
                continue;
            }

            foreach (var errorState in month.Values)
            {
                if (ErrorRecordIlpMapper.TryParse(errorState, out var record))
                {
                    records.Add(record);
                }
            }
        }

        return true;
    }

    public bool TryReadSequence(out long value)
    {
        value = 0;

        if (!Ilp.TryGetState(Constants.Ilp.Paths.ERROR_NUMBER, out var state) || state is not LongState sequenceState)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX}[{nameof(TryReadSequence)}] Sequence not found or not a longState: {Constants.Ilp.Paths.ERROR_NUMBER}");
            return false;
        }

        value = sequenceState.Value;
        return true;
    }

    public bool TryEnsureSequenceAtLeast(long value)
    {
        if (!TryReadSequence(out var current))
        {
            return false;
        }

        if (current >= value)
        {
            return true;
        }

        var changeRequest = new ChangeRequest(Ilp);
        AddSequenceChange(changeRequest, value);
        return Send(changeRequest, "raise sequence");
    }

    public bool TryCreate(ErrorRecord record, long newSequenceValue)
    {
        var changeRequest = new ChangeRequest(Ilp);

        AddSequenceChange(changeRequest, newSequenceValue);

        if (!Ilp.TryGetState(ErrorRecordIlpMapper.GetMonthPath(record), out _))
        {
            changeRequest.CreateState(new DictionaryState<StringState, IBaseState>(ErrorRecordIlpMapper.GetMonthPath(record)));
        }

        changeRequest.CreateState(ErrorRecordIlpMapper.ToIlpState(record));

        return Send(changeRequest, $"create error {record.ErrorNumber}");
    }

    public bool TryUpdate(ErrorRecord record)
    {
        var path = ErrorRecordIlpMapper.GetPath(record);
        if (!Ilp.TryGetState(path, out var state) || state is not DictionaryState<StringState, IBaseState> errorState)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX}[{nameof(TryUpdate)}] Error {record.Id} not found on ilp: {path}");
            return false;
        }

        ErrorRecordIlpMapper.ApplyMutableFields(errorState, record);

        var changeRequest = new ChangeRequest(Ilp);
        changeRequest.ChangeState(errorState);

        return Send(changeRequest, $"update error {record.ErrorNumber}");
    }

    public bool TryDelete(ErrorRecord record)
    {
        var path = ErrorRecordIlpMapper.GetPath(record);
        if (!Ilp.TryGetState(path, out var state))
        {
            Logging.Log(LogLevel.Warning, $"{LOG_PREFIX}[{nameof(TryDelete)}] Error {record.Id} is already gone from ilp: {path}");
            return true;
        }

        var changeRequest = new ChangeRequest(Ilp);

        if (Ilp.TryGetState(ErrorRecordIlpMapper.GetMonthPath(record), out var monthState)
            && monthState is DictionaryState<StringState, IBaseState> month
            && month.Count <= 1)
        {
            changeRequest.DeleteState(month);
        }
        else
        {
            changeRequest.DeleteState(state);
        }

        return Send(changeRequest, $"delete error {record.ErrorNumber}");
    }

    private void AddSequenceChange(ChangeRequest changeRequest, long value)
    {
        if (Ilp.TryGetState(Constants.Ilp.Paths.ERROR_NUMBER, out var state) && state is LongState sequenceState)
        {
            sequenceState.Value = value;
            changeRequest.ChangeState(sequenceState);
        }
        else
        {
            changeRequest.CreateState(new LongState(Constants.Ilp.Paths.ERROR_NUMBER, value));
        }
    }

    private bool Send(ChangeRequest changeRequest, string description)
    {
        var result = Ilp.SendChangeRequest(changeRequest);
        if (result != ExecutionResponse.SUCCEEDED)
        {
            Logging.Log(LogLevel.Error, $"{LOG_PREFIX} Cannot {description} on ilp: {result}");
            return false;
        }

        return true;
    }
}

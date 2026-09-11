using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Prometheus;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Player;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Robust.Server.GameStates;

internal sealed partial class PvsSystem
{
    [Dependency] private readonly IRobustSerializer _serializer = default!;

    /// <summary>
    /// Get and serialize <see cref="GameState"/> objects for each player. Compressing & sending the states is done later.
    /// </summary>
    private void SerializeStates()
    {
        using var _ = Histogram.WithLabels("Serialize States").NewTimer();
        var opts = new ParallelOptions {MaxDegreeOfParallelism = _parallelMgr.ParallelProcessCount};
        _oldestAck = GameTick.MaxValue.Value;
        // DS14-start
        try
        {
            Parallel.For(-1, _sessions.Length, opts, SerializeState);
        }
        finally
        {
            // Workers may have partially updated visibility or committed history before serialization failed.
            // Reset only affected player sessions, after all serialization workers have finished.
            foreach (var session in _sessions)
            {
                if (session.SerializationFailed)
                    ForceFullState(session);
            }
        }
        // DS14-end
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session (or the current replay).
    /// </summary>
    private void SerializeState(int i)
    {
        try
        {
            var guid = i >= 0 ? _sessions[i].Session.UserId.UserId : default;
            ServerGameStateManager.PvsEventSource.Log.WorkStart(_gameTiming.CurTick.Value, i, guid);

            if (i >= 0)
                SerializeSessionState(_sessions[i]);
            else
                _replay.Update();

            ServerGameStateManager.PvsEventSource.Log.WorkStop(_gameTiming.CurTick.Value, i, guid);
        }
        catch (Exception e) // Catch EVERY exception
        {
            var source = i >= 0 ? _sessions[i].Session.ToString() : "replays";
            Log.Log(LogLevel.Error, e, $"Caught exception while serializing game state for {source}.");
#if !EXCEPTION_TOLERANCE
            throw;
#endif
        }
    }

    /// <summary>
    /// Get and serialize a <see cref="GameState"/> for a single session.
    /// </summary>
    private void SerializeSessionState(PvsSession data)
    {
        data.SerializationFailed = false; // DS14
        var serialized = false;

        try
        {
            ComputeSessionState(data);
            InterlockedHelper.Min(ref _oldestAck, data.FromTick.Value);
            DebugTools.AssertEqual(data.StateStream, null);

            // PVS benchmarks use dummy sessions.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (data.Session.Channel is not DummyChannel)
            {
                data.StateStream = RobustMemoryManager.GetMemoryStream();
                _serializer.SerializeDirect(data.StateStream, data.State);
            }

            serialized = true;
        }
        finally
        {
            if (!serialized)
            {
                data.SerializationFailed = true; // DS14
                data.StateStream?.Dispose();
                data.StateStream = null;
                // DS14-start
                data.LastSent = null;
                if (data.ToSend is { } incomplete)
                {
                    // A completed list already belongs to PreviouslySent and is released by ForceFullState.
                    _entDataListPool.Return(incomplete);
                    data.ToSend = null;
                }
                // DS14-end
            }

            ReleasePooledStateData(data);
            data.ClearState();
        }
    }

    private void ReleasePooledStateData(PvsSession data)
    {
        var states = data.States;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var changes = state.ComponentChanges.Value;

            if (changes is List<ComponentChange> list)
                _componentChangeListPool.Return(list);

            if (state.NetComponents == null)
                continue;

            _netComponentSetPool.Return(state.NetComponents);
            state.NetComponents = null;
        }
    }
}

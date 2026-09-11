// Мёртвый Космос, Licensed under custom terms with restrictions on public hosting and commercial use, full text: https://raw.githubusercontent.com/dead-space-server/space-station-14-fobos/master/LICENSE.TXT

using System.Reflection;
using System.Numerics;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Map;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Serilog.Events;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class PvsSerializationRecoveryTest : RobustIntegrationTest
{
    internal const string InjectedFailure = "Injected PVS serialization failure.";

    private const string Prototypes = """
        - type: entity
          id: PvsSerializationRecoveryStatic
          components:
          - type: PvsSerializationRecoveryTest
        """;

    [TestCase(true)]
    [TestCase(false)]
    public async Task ComponentFailurePreservesStaticEntityAndResendsFullState(bool culling)
    {
        var logs = new LogCatcher();
        var server = StartServer(new ServerIntegrationOptions
        {
            Pool = false,
            ExtraPrototypes = Prototypes,
            OverrideLogHandler = () => logs,
        });
        var client = StartClient(new ClientIntegrationOptions { Pool = false, ExtraPrototypes = Prototypes });
        await Task.WhenAll(server.WaitIdleAsync(), client.WaitIdleAsync());

        await server.WaitPost(() => server.CfgMan.SetCVar(CVars.NetPVS, culling));
        client.SetConnectTarget(server);
        var net = client.ResolveDependency<IClientNetManager>();
        await client.WaitPost(() => net.ClientConnect(null!, 0, null!));
        for (var i = 0; i < 10; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }

        EntityUid map = default;
        EntityUid stationary = default;
        ICommonSession session = default!;
        PvsSerializationRecoveryTestComponent probe = default!;
        await server.WaitPost(() =>
        {
            map = server.System<SharedMapSystem>().CreateMap();
            var player = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            stationary = server.EntMan.SpawnEntity("PvsSerializationRecoveryStatic", new EntityCoordinates(map, Vector2.One));
            probe = server.EntMan.GetComponent<PvsSerializationRecoveryTestComponent>(stationary);
            session = server.PlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            server.PlayerMan.JoinGame(session);
        });

        for (var i = 0; i < 10; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }

        var netMap = server.EntMan.GetNetEntity(map);
        var netStationary = server.EntMan.GetNetEntity(stationary);
        await client.WaitPost(() => AssertStillVisible(client, netStationary, netMap));

        await server.WaitPost(() =>
        {
            probe.TargetSession = session;
            probe.FailNextState = true;
            server.EntMan.Dirty(stationary, probe);

            var pvs = server.System<PvsSystem>();
            SendExpectingFailure(pvs, session);
            var data = pvs.PlayerData[session];
            Assert.That(data.SerializationFailed, Is.True);
            Assert.That(data.ToSend, Is.Null);
            Assert.That(data.StateStream, Is.Null);
            Assert.That(data.LastSent, Is.Null);
            Assert.That(data.PreviouslySent, Is.Empty);
            Assert.That(data.RequestedFull, Is.True);
        });

        // The static entity is never dirtied again. Recovery must restore it without relying on movement updates.
        for (var i = 0; i < 12; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
            await client.WaitPost(() => AssertStillVisible(client, netStationary, netMap));
        }

        await server.WaitPost(() =>
        {
            Assert.That(probe.FullStatesAfterFailure, Is.GreaterThan(0));
            Assert.That(server.System<PvsSystem>().PlayerData[session].SerializationFailed, Is.False);
        });
        await client.WaitPost(() => net.ClientDisconnect(""));
        await server.WaitRunTicks(5);
        AssertExpectedFailureLog(logs);
    }

    [Test]
    public async Task FailedDummySessionIsNotAcknowledged()
    {
        var logs = new LogCatcher();
        var server = StartServer(new ServerIntegrationOptions
        {
            Pool = false,
            ExtraPrototypes = Prototypes,
            OverrideLogHandler = () => logs,
        });
        await server.WaitIdleAsync();
        var session = await server.AddDummySession();
        PvsSerializationRecoveryTestComponent probe = default!;
        EntityUid stationary = default;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, true);
            var map = server.System<SharedMapSystem>().CreateMap();
            stationary = server.EntMan.SpawnEntity("PvsSerializationRecoveryStatic", new EntityCoordinates(map, Vector2.Zero));
            probe = server.EntMan.GetComponent<PvsSerializationRecoveryTestComponent>(stationary);
            server.PlayerMan.SetAttachedEntity(session, stationary);
            server.PlayerMan.JoinGame(session);
        });
        await server.WaitRunTicks(3);

        await server.WaitPost(() =>
        {
            probe.TargetSession = session;
            probe.FailNextState = true;
            server.EntMan.Dirty(stationary, probe);
            var pvs = server.System<PvsSystem>();
            var previousAck = pvs.PlayerData[session].LastReceivedAck;
            SendExpectingFailure(pvs, session);

            // Debug normally rethrows before the send phase; exercise the same skip as a tolerant Release build.
            InvokePhase(pvs, "SendStates");
            var data = pvs.PlayerData[session];
            Assert.That(data.SerializationFailed, Is.True);
            Assert.That(data.RequestedFull, Is.True, "A dummy ACK must not cancel recovery of a failed state.");
            Assert.That(data.PreviouslySent, Is.Empty);

            InvokePhase(pvs, "OnClientAck", session, previousAck);
            InvokePhase(pvs, "ProcessQueuedAck", data);
            Assert.That(data.RequestedFull, Is.True, "A late or already queued ACK must not acknowledge the failed tick.");
        });

        await server.WaitRunTicks(1);
        await server.WaitPost(() =>
        {
            var data = server.System<PvsSystem>().PlayerData[session];
            Assert.That(data.SerializationFailed, Is.False);
            Assert.That(data.RequestedFull, Is.False, "A successful dummy state must still be acknowledged.");
            Assert.That(probe.FullStatesAfterFailure, Is.GreaterThan(0));
        });
        await server.RemoveDummySession(session);
        AssertExpectedFailureLog(logs);
    }

    [Test]
    public async Task MissingPreviousTickDoesNotReuseLastSent()
    {
        var server = StartServer(new ServerIntegrationOptions { Pool = false });
        await server.WaitIdleAsync();
        var session = await server.AddDummySession();
        EntityUid stationary = default;
        await server.WaitPost(() =>
        {
            server.CfgMan.SetCVar(CVars.NetPVS, true);
            var map = server.System<SharedMapSystem>().CreateMap();
            stationary = server.EntMan.SpawnEntity(null, new EntityCoordinates(map, Vector2.Zero));
            server.PlayerMan.SetAttachedEntity(session, stationary);
            server.PlayerMan.JoinGame(session);
        });
        await server.WaitRunTicks(3);

        await server.WaitPost(() =>
        {
            var pvs = server.System<PvsSystem>();
            pvs.ProcessDisconnections();
            var data = pvs.PlayerData[session];
            InvokePhase(pvs, "ForceFullState", data);
            var pointer = server.EntMan.GetComponent<MetaDataComponent>(stationary).PvsData;
            data.LastSent = (server.Timing.CurTick - 1, new List<PvsIndex> { pointer });

            pvs.SendGameStates(new[] { session });
            pvs.ProcessDisconnections();
            Assert.That(data.LastSent, Is.Null, "Without the preceding tick, an old leave-PVS baseline is invalid.");
        });
        await server.RemoveDummySession(session);
    }

    private static void AssertStillVisible(ClientIntegrationInstance client, NetEntity entity, NetEntity map)
    {
        Assert.That(client.EntMan.TryGetEntity(entity, out var uid), Is.True);
        var metadata = client.EntMan.GetComponent<MetaDataComponent>(uid!.Value);
        Assert.That(metadata.Flags & MetaDataFlags.Detached, Is.EqualTo(MetaDataFlags.None));
        Assert.That(client.EntMan.GetNetEntity(client.Transform(uid.Value).ParentUid), Is.EqualTo(map));
    }

    private static void SendExpectingFailure(PvsSystem pvs, ICommonSession session)
    {
        Exception? failure = null;
        try
        {
            pvs.SendGameStates(new[] { session });
        }
        catch (Exception exception)
        {
            failure = exception;
        }

#if EXCEPTION_TOLERANCE
        Assert.That(failure, Is.Null);
#else
        Assert.That(failure, Is.TypeOf<AggregateException>());
        Assert.That(failure!.ToString(), Does.Contain(InjectedFailure));
#endif
    }

    private static void AssertExpectedFailureLog(LogCatcher logs)
    {
        var errors = logs.CaughtLogs.Where(log => log.Level >= LogEventLevel.Error).ToArray();
        Assert.That(errors, Has.Length.EqualTo(1), "Only the deliberately injected serialization error is expected.");
        Assert.That(errors[0].RenderMessage(), Does.StartWith("Caught exception while serializing game state for "));
        Assert.That(errors[0].Exception?.ToString(), Does.Contain(InjectedFailure));
        Assert.That(errors[0].Exception?.ToString(), Does.Contain(nameof(PvsSerializationRecoveryTestComponent)));
    }

    // Exercise existing private phase boundaries without exposing production APIs solely for fault injection.
    private static void InvokePhase(PvsSystem pvs, string name, params object[] arguments)
    {
        var method = typeof(PvsSystem).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(method, Is.Not.Null);
        method!.Invoke(pvs, arguments);
    }
}

[RegisterComponent, NetworkedComponent]
internal sealed partial class PvsSerializationRecoveryTestComponent : Component
{
    public ICommonSession? TargetSession;
    public bool FailNextState;
    public int FullStatesAfterFailure;
}

internal sealed class PvsSerializationRecoveryTestSystem : EntitySystem
{
    public override void Initialize()
    {
        SubscribeLocalEvent<PvsSerializationRecoveryTestComponent, ComponentGetState>(OnGetState);
    }

    private void OnGetState(Entity<PvsSerializationRecoveryTestComponent> ent, ref ComponentGetState args)
    {
        if (args.Player == null || args.Player != ent.Comp.TargetSession)
            return;

        if (ent.Comp.FailNextState)
        {
            ent.Comp.FailNextState = false;
            throw new InvalidOperationException(PvsSerializationRecoveryTest.InjectedFailure);
        }

        if (args.FromTick == GameTick.Zero)
            ent.Comp.FullStatesAfterFailure++;
    }
}

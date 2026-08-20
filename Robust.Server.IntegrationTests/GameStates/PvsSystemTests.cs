using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Robust.Server.GameStates;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Robust.UnitTesting.Server.GameStates;

public sealed class PvsSystemTests : RobustIntegrationTest
{
    /// <summary>
    /// Checks that overlapping global overrides still cache branches that were not traversed by an earlier child override.
    /// </summary>
    [Test]
    public async Task TestOverlappingGlobalOverrides()
    {
        var server = StartServer();
        var client = StartClient();

        await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var pvsOverride = sEntMan.System<PvsOverrideSystem>();

        var cEntMan = client.ResolveDependency<IEntityManager>();
        var netMan = client.ResolveDependency<IClientNetManager>();

        Assert.DoesNotThrow(() => client.SetConnectTarget(server));
        client.Post(() => netMan.ClientConnect(null!, 0, null!));
        server.Post(() => confMan.SetCVar(CVars.NetPVS, true));

        for (var i = 0; i < 10; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }

        EntityUid root = default;
        EntityUid overriddenLeaf = default;
        EntityUid sibling = default;
        await server.WaitPost(() =>
        {
            var playerMap = server.System<SharedMapSystem>().CreateMap();
            var overrideMap = server.System<SharedMapSystem>().CreateMap();

            var player = sEntMan.SpawnEntity(null, new EntityCoordinates(playerMap, Vector2.Zero));
            root = sEntMan.SpawnEntity(null, new EntityCoordinates(overrideMap, Vector2.Zero));
            var branch = sEntMan.SpawnEntity(null, new EntityCoordinates(root, Vector2.Zero));
            overriddenLeaf = sEntMan.SpawnEntity(null, new EntityCoordinates(branch, Vector2.Zero));
            sibling = sEntMan.SpawnEntity(null, new EntityCoordinates(root, Vector2.One));

            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);

            // Insert the descendant first. Its parent chain is cached, but the sibling branch is not traversed yet.
            pvsOverride.AddGlobalOverride(overriddenLeaf);
            pvsOverride.AddGlobalOverride(root);
        });

        for (var i = 0; i < 10; i++)
        {
            await server.WaitRunTicks(1);
            await client.WaitRunTicks(1);
        }

        Assert.That(cEntMan.TryGetEntity(sEntMan.GetNetEntity(root), out _));
        Assert.That(cEntMan.TryGetEntity(sEntMan.GetNetEntity(overriddenLeaf), out _));
        Assert.That(cEntMan.TryGetEntity(sEntMan.GetNetEntity(sibling), out _));

        await client.WaitPost(() => netMan.ClientDisconnect(""));
        await server.WaitRunTicks(5);
        await client.WaitRunTicks(5);
    }

    /// <summary>
    /// Checks that there are no issues when an entity changes PVS chunk location multiple times in a single tick.
    /// </summary>
    [Test]
    public async Task TestMultipleIndexChange()
    {
        await using var pair = await StartConnectedPair();
        var (client, server) = pair;

        var sEntMan = server.ResolveDependency<IEntityManager>();
        var confMan = server.ResolveDependency<IConfigurationManager>();
        var sPlayerMan = server.ResolveDependency<ISharedPlayerManager>();
        var xforms = sEntMan.System<SharedTransformSystem>();
        var maps = sEntMan.System<SharedMapSystem>();

        var cEntMan = client.ResolveDependency<IEntityManager>();
        var cPlayerMan = client.ResolveDependency<ISharedPlayerManager>();

        server.Post(() => confMan.SetCVar(CVars.NetPVS, true));

        await RunTicksSync(server, client, 10);

        // Set up map and grid
        EntityUid grid = default;
        EntityUid map = default;
        await server.WaitPost(() =>
        {
            map = server.System<SharedMapSystem>().CreateMap(out var mapId);
            var gridComp = maps.CreateGridEntity(mapId);
            maps.SetTile(gridComp, Vector2i.Zero, new Tile(1));
            grid = gridComp.Owner;
        });

        // Spawn player entity on grid 1
        EntityUid player = default;
        EntityUid other = default;
        TransformComponent otherXform = default!;
        var gridCoords = new EntityCoordinates(grid, new Vector2(0.5f, 0.5f));
        var mapCoords = new EntityCoordinates(map, new Vector2(2, 2));
        await server.WaitPost(() =>
        {
            player = sEntMan.SpawnEntity(null, gridCoords);
            other = sEntMan.SpawnEntity(null, gridCoords);
            otherXform = sEntMan.GetComponent<TransformComponent>(other);

            // Ensure map PVS chunk is not empty
            sEntMan.SpawnEntity(null, mapCoords);

            // Attach player.
            var session = sPlayerMan.Sessions.First();
            server.PlayerMan.SetAttachedEntity(session, player);
            sPlayerMan.JoinGame(session);
        });

        await RunTicksSync(server, client, 10);

        // Check player got properly attached
        await client.WaitPost(() =>
        {
            var ent = cEntMan.GetNetEntity(cPlayerMan.LocalEntity);
            Assert.That(ent, Is.EqualTo(sEntMan.GetNetEntity(player)));
        });

        // Move the player off-grid and back onto the grid in the same tick
        xforms.SetCoordinates(other, otherXform, mapCoords);
        xforms.SetCoordinates(other, otherXform, gridCoords);

        // Run for a few ticks. The test just checks that no PVS asserts/errors happen.
        await RunTicksSync(server, client, 10);

        // Repeat but in the opposite direction ( map -> grid -> map )
        // first move to map and wait a bit.
        xforms.SetCoordinates(other, otherXform, mapCoords);
        await RunTicksSync(server, client, 10);

        // Move to and off grid in the same tick
        xforms.SetCoordinates(other, otherXform, gridCoords);
        xforms.SetCoordinates(other, otherXform, mapCoords);

        // wait for errors.
        await RunTicksSync(server, client, 10);

    }
}


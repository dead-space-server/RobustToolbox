using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Robust.Shared.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Dynamics.Contacts;

namespace Robust.Shared.Physics.Systems;

public abstract partial class SharedPhysicsSystem
{
    public string BuildPhysicsDiagnosticsReport(int limit)
    {
        limit = Math.Clamp(limit, 1, 200);

        var bodyGroups = new Dictionary<string, BodyGroupDiagnostics>(StringComparer.Ordinal);
        var bodyStateGroups = new Dictionary<string, BodyStateDiagnostics>(StringComparer.Ordinal);
        var bodyLines = new List<BodyDiagnostics>(AwakeBodies.Count);
        var hardContactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var activeContactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var touchingContactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var contactBodyGroups = new Dictionary<string, ContactBodyDiagnostics>(StringComparer.Ordinal);
        var inactiveContactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var inactiveTouchingContactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var inactiveContactBodyGroups = new Dictionary<string, ContactBodyDiagnostics>(StringComparer.Ordinal);
        var inactiveContactStateGroups = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var seenContacts = new HashSet<ContactKey>();

        var hardContactCount = 0;
        var touchingContactCount = 0;
        var sleepingDisabledCount = 0;
        var inAirCount = 0;
        var settledCount = 0;
        var sleepReadyCount = 0;
        var zeroContactCount = 0;
        var zeroContactSettledCount = 0;
        var zeroContactSleepReadyCount = 0;
        var zeroContactOnGroundCount = 0;
        var maxSleepTime = 0f;
        var activeContacts = 0;
        var activeTouchingContacts = 0;
        var activeHardContacts = 0;
        var inactiveContacts = 0;
        var inactiveTouchingContacts = 0;
        var inactiveHardContacts = 0;
        var inactiveSensorContacts = 0;
        var inactiveBothStaticContacts = 0;
        var inactiveStaticSleepingContacts = 0;
        var inactiveBothSleepingContacts = 0;
        var disabledContacts = 0;
        var deletingContacts = 0;
        var sensorContacts = 0;
        var preInitContacts = 0;
        var filterContacts = 0;
        var gridContacts = 0;

        foreach (var ent in AwakeBodies)
        {
            var uid = ent.Owner;
            var body = ent.Comp1;
            var xform = ent.Comp2;

            if (Deleted(uid))
                continue;

            var prototype = GetPrototype(uid);
            var name = GetEntityName(uid);
            var speed = body.LinearVelocity.Length();
            var contacts = body.ContactCount;
            var touchingContacts = 0;
            var hardContacts = 0;
            var settled = IsSettled(body);
            var sleepReady = body.SleepTime >= TimeToSleep;

            maxSleepTime = Math.Max(maxSleepTime, body.SleepTime);

            if (!body.SleepingAllowed)
                sleepingDisabledCount++;

            if (body.BodyStatus == BodyStatus.InAir)
                inAirCount++;

            if (settled)
                settledCount++;

            if (sleepReady)
                sleepReadyCount++;

            if (contacts == 0)
            {
                zeroContactCount++;

                if (settled)
                    zeroContactSettledCount++;

                if (sleepReady)
                    zeroContactSleepReadyCount++;

                if (body.BodyStatus == BodyStatus.OnGround)
                    zeroContactOnGroundCount++;
            }

            if (TryComp<FixturesComponent>(uid, out var fixtures))
            {
                var contactEnumerator = GetContacts((uid, fixtures));

                while (contactEnumerator.MoveNext(out var contact))
                {
                    if (!contact.Enabled || contact.Deleting || !contact.IsTouching)
                        continue;

                    touchingContacts++;

                    if (!contact.Hard)
                        continue;

                    hardContacts++;

                    var key = ContactKey.Create(contact);
                    if (!seenContacts.Add(key))
                        continue;

                    hardContactCount++;
                    AddContactPair(hardContactPairs, contact);
                }
            }

            touchingContactCount += touchingContacts;

            if (!bodyGroups.TryGetValue(prototype, out var group))
            {
                group = new BodyGroupDiagnostics(prototype);
                bodyGroups.Add(prototype, group);
            }

            group.Count++;
            group.Contacts += contacts;
            group.TouchingContacts += touchingContacts;
            group.HardContacts += hardContacts;
            group.SleepingDisabled += body.SleepingAllowed ? 0 : 1;
            group.InAir += body.BodyStatus == BodyStatus.InAir ? 1 : 0;
            group.ZeroContacts += contacts == 0 ? 1 : 0;
            group.ZeroContactSettled += contacts == 0 && settled ? 1 : 0;
            group.SleepReady += sleepReady ? 1 : 0;
            group.MaxSleepTime = Math.Max(group.MaxSleepTime, body.SleepTime);
            group.MaxSpeed = Math.Max(group.MaxSpeed, speed);

            var stateKey = $"{body.BodyType}/{body.BodyStatus}";
            if (!bodyStateGroups.TryGetValue(stateKey, out var stateGroup))
            {
                stateGroup = new BodyStateDiagnostics(stateKey);
                bodyStateGroups.Add(stateKey, stateGroup);
            }

            stateGroup.Count++;
            stateGroup.Contacts += contacts;
            stateGroup.TouchingContacts += touchingContacts;
            stateGroup.HardContacts += hardContacts;
            stateGroup.ZeroContacts += contacts == 0 ? 1 : 0;
            stateGroup.Settled += settled ? 1 : 0;
            stateGroup.SleepReady += sleepReady ? 1 : 0;
            stateGroup.MaxSleepTime = Math.Max(stateGroup.MaxSleepTime, body.SleepTime);
            stateGroup.MaxSpeed = Math.Max(stateGroup.MaxSpeed, speed);

            bodyLines.Add(new BodyDiagnostics(
                uid,
                prototype,
                name,
                xform.MapID.ToString(),
                xform.GridUid?.ToString() ?? "none",
                body.BodyType.ToString(),
                body.BodyStatus.ToString(),
                body.CanCollide,
                body.Hard,
                body.SleepingAllowed,
                body.FixturesMass,
                speed,
                Math.Abs(body.AngularVelocity),
                body.SleepTime,
                settled,
                sleepReady,
                contacts,
                touchingContacts,
                hardContacts));
        }

        foreach (var contact in _activeContacts)
        {
            if (!contact.Enabled)
            {
                disabledContacts++;
                continue;
            }

            if ((contact.Flags & ContactFlags.Deleting) != 0)
            {
                deletingContacts++;
                continue;
            }

            if ((contact.Flags & ContactFlags.PreInit) != 0)
                preInitContacts++;

            if ((contact.Flags & ContactFlags.Filter) != 0)
                filterContacts++;

            if ((contact.Flags & ContactFlags.Grid) != 0)
                gridContacts++;

            if (!contact.Hard)
                sensorContacts++;

            var bodyA = contact.BodyA;
            var bodyB = contact.BodyB;
            if (bodyA == null || bodyB == null)
                continue;

            var activeA = bodyA.Awake && bodyA.BodyType != BodyType.Static;
            var activeB = bodyB.Awake && bodyB.BodyType != BodyType.Static;
            if (!activeA && !activeB)
            {
                inactiveContacts++;
                inactiveTouchingContacts += contact.IsTouching ? 1 : 0;
                inactiveHardContacts += contact.Hard ? 1 : 0;
                inactiveSensorContacts += contact.Hard ? 0 : 1;

                if (bodyA.BodyType == BodyType.Static && bodyB.BodyType == BodyType.Static)
                {
                    inactiveBothStaticContacts++;
                }
                else if (bodyA.BodyType == BodyType.Static || bodyB.BodyType == BodyType.Static)
                {
                    inactiveStaticSleepingContacts++;
                }
                else
                {
                    inactiveBothSleepingContacts++;
                }

                AddContactPair(inactiveContactPairs, contact);
                AddContactBody(inactiveContactBodyGroups, contact.EntityA, bodyA, contact);
                AddContactBody(inactiveContactBodyGroups, contact.EntityB, bodyB, contact);
                AddContactState(inactiveContactStateGroups, contact, bodyA, bodyB);

                if (contact.IsTouching)
                    AddContactPair(inactiveTouchingContactPairs, contact);

                continue;
            }

            activeContacts++;
            AddContactPair(activeContactPairs, contact);
            AddContactBody(contactBodyGroups, contact.EntityA, bodyA, contact);
            AddContactBody(contactBodyGroups, contact.EntityB, bodyB, contact);

            if (contact.IsTouching)
            {
                activeTouchingContacts++;
                AddContactPair(touchingContactPairs, contact);
            }

            if (contact.Hard)
                activeHardContacts++;
        }

        var builder = new StringBuilder();
        builder.AppendLine("physics diagnostics");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"awake={bodyLines.Count}, hardContacts={hardContactCount}, touchingContactRefs={touchingContactCount}, movedGrids={MovedGrids.Count}, moveBuffer={MoveBuffer.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"sleepingDisabled={sleepingDisabledCount}, inAir={inAirCount}, settled={settledCount}, sleepReady={sleepReadyCount}, maxSleep={maxSleepTime:0.###}, limit={limit}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"zeroContact={zeroContactCount}, zeroContactSettled={zeroContactSettledCount}, zeroContactSleepReady={zeroContactSleepReadyCount}, zeroContactOnGround={zeroContactOnGroundCount}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"contacts total={ContactCount}, active={activeContacts}, activeTouching={activeTouchingContacts}, activeHard={activeHardContacts}, inactive={inactiveContacts}, disabled={disabledContacts}, deleting={deletingContacts}, sensors={sensorContacts}, preInit={preInitContacts}, filter={filterContacts}, grid={gridContacts}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"inactive contacts touching={inactiveTouchingContacts}, hard={inactiveHardContacts}, sensors={inactiveSensorContacts}, bothStatic={inactiveBothStaticContacts}, staticSleeping={inactiveStaticSleepingContacts}, bothSleeping={inactiveBothSleepingContacts}");

        builder.AppendLine();
        builder.AppendLine("awake body states:");
        foreach (var group in bodyStateGroups.Values
                     .OrderByDescending(group => group.Count)
                     .ThenBy(group => group.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.Count,5} {group.Label} contacts={group.Contacts} touching={group.TouchingContacts} hard={group.HardContacts} zero={group.ZeroContacts} settled={group.Settled} ready={group.SleepReady} maxSleep={group.MaxSleepTime:0.###} maxSpeed={group.MaxSpeed:0.###}");
        }

        builder.AppendLine();
        builder.AppendLine("top awake prototypes:");
        foreach (var group in bodyGroups.Values
                     .OrderByDescending(group => group.Count)
                     .ThenByDescending(group => group.HardContacts)
                     .ThenBy(group => group.Prototype, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.Count,5} {group.Prototype} contacts={group.Contacts} touching={group.TouchingContacts} hard={group.HardContacts} zero={group.ZeroContacts} zeroSettled={group.ZeroContactSettled} ready={group.SleepReady} inAir={group.InAir} sleepOff={group.SleepingDisabled} maxSleep={group.MaxSleepTime:0.###} maxSpeed={group.MaxSpeed:0.###}");
        }

        builder.AppendLine();
        builder.AppendLine("top zero-contact settled prototypes:");
        foreach (var group in bodyGroups.Values
                     .Where(group => group.ZeroContactSettled > 0)
                     .OrderByDescending(group => group.ZeroContactSettled)
                     .ThenByDescending(group => group.MaxSleepTime)
                     .ThenBy(group => group.Prototype, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.ZeroContactSettled,5} {group.Prototype} total={group.Count} zero={group.ZeroContacts} ready={group.SleepReady} maxSleep={group.MaxSleepTime:0.###} maxSpeed={group.MaxSpeed:0.###}");
        }

        builder.AppendLine();
        builder.AppendLine("top awake bodies by hard contacts:");
        foreach (var body in bodyLines
                     .OrderByDescending(body => body.HardContacts)
                     .ThenByDescending(body => body.TouchingContacts)
                     .ThenByDescending(body => body.Speed)
                     .ThenBy(body => body.Uid.Id)
                     .Take(limit))
        {
            AppendBodyLine(builder, body);
        }

        builder.AppendLine();
        builder.AppendLine("top awake bodies by speed:");
        foreach (var body in bodyLines
                     .OrderByDescending(body => body.Speed)
                     .ThenByDescending(body => body.AngularSpeed)
                     .ThenBy(body => body.Uid.Id)
                     .Take(limit))
        {
            AppendBodyLine(builder, body);
        }

        builder.AppendLine();
        builder.AppendLine("top hard contact pairs:");
        foreach (var pair in hardContactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label}");
        }

        builder.AppendLine();
        builder.AppendLine("top active contact pairs:");
        foreach (var pair in activeContactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenByDescending(pair => pair.Touching)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label} touching={pair.Touching} hard={pair.Hard} sensors={pair.Sensor}");
        }

        builder.AppendLine();
        builder.AppendLine("top active touching contact pairs:");
        foreach (var pair in touchingContactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenByDescending(pair => pair.Hard)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label} hard={pair.Hard} sensors={pair.Sensor}");
        }

        builder.AppendLine();
        builder.AppendLine("top active contact body prototypes:");
        foreach (var group in contactBodyGroups.Values
                     .OrderByDescending(group => group.ContactRefs)
                     .ThenByDescending(group => group.TouchingRefs)
                     .ThenBy(group => group.Prototype, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.ContactRefs,5} {group.Prototype} bodies={group.Bodies.Count} touchingRefs={group.TouchingRefs} hardRefs={group.HardRefs} awakeRefs={group.AwakeRefs}");
        }

        builder.AppendLine();
        builder.AppendLine("top inactive contact states:");
        foreach (var pair in inactiveContactStateGroups.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenByDescending(pair => pair.Touching)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label} touching={pair.Touching} hard={pair.Hard} sensors={pair.Sensor}");
        }

        builder.AppendLine();
        builder.AppendLine("top inactive contact pairs:");
        foreach (var pair in inactiveContactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenByDescending(pair => pair.Touching)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label} touching={pair.Touching} hard={pair.Hard} sensors={pair.Sensor}");
        }

        builder.AppendLine();
        builder.AppendLine("top inactive touching contact pairs:");
        foreach (var pair in inactiveTouchingContactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenByDescending(pair => pair.Hard)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label} hard={pair.Hard} sensors={pair.Sensor}");
        }

        builder.AppendLine();
        builder.AppendLine("top inactive contact body prototypes:");
        foreach (var group in inactiveContactBodyGroups.Values
                     .OrderByDescending(group => group.ContactRefs)
                     .ThenByDescending(group => group.TouchingRefs)
                     .ThenBy(group => group.Prototype, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.ContactRefs,5} {group.Prototype} bodies={group.Bodies.Count} touchingRefs={group.TouchingRefs} hardRefs={group.HardRefs} awakeRefs={group.AwakeRefs}");
        }

        return builder.ToString();
    }

    private void AddContactPair(Dictionary<string, ContactPairDiagnostics> contactPairs, Contact contact)
    {
        var prototypeA = GetPrototype(contact.EntityA);
        var prototypeB = GetPrototype(contact.EntityB);

        if (string.CompareOrdinal(prototypeA, prototypeB) > 0)
            (prototypeA, prototypeB) = (prototypeB, prototypeA);

        var label = $"{prototypeA} <-> {prototypeB}";
        if (!contactPairs.TryGetValue(label, out var pair))
        {
            pair = new ContactPairDiagnostics(label);
            contactPairs.Add(label, pair);
        }

        pair.Count++;
        pair.Touching += contact.IsTouching ? 1 : 0;
        pair.Hard += contact.Hard ? 1 : 0;
        pair.Sensor += contact.Hard ? 0 : 1;
    }

    private void AddContactBody(
        Dictionary<string, ContactBodyDiagnostics> contactBodyGroups,
        EntityUid uid,
        PhysicsComponent body,
        Contact contact)
    {
        var prototype = GetPrototype(uid);
        if (!contactBodyGroups.TryGetValue(prototype, out var group))
        {
            group = new ContactBodyDiagnostics(prototype);
            contactBodyGroups.Add(prototype, group);
        }

        group.Bodies.Add(uid);
        group.ContactRefs++;
        group.TouchingRefs += contact.IsTouching ? 1 : 0;
        group.HardRefs += contact.Hard ? 1 : 0;
        group.AwakeRefs += body.Awake ? 1 : 0;
    }

    private void AddContactState(
        Dictionary<string, ContactPairDiagnostics> contactStates,
        Contact contact,
        PhysicsComponent bodyA,
        PhysicsComponent bodyB)
    {
        var stateA = GetContactStateLabel(bodyA);
        var stateB = GetContactStateLabel(bodyB);

        if (string.CompareOrdinal(stateA, stateB) > 0)
            (stateA, stateB) = (stateB, stateA);

        var label = $"{stateA} <-> {stateB}";
        if (!contactStates.TryGetValue(label, out var pair))
        {
            pair = new ContactPairDiagnostics(label);
            contactStates.Add(label, pair);
        }

        pair.Count++;
        pair.Touching += contact.IsTouching ? 1 : 0;
        pair.Hard += contact.Hard ? 1 : 0;
        pair.Sensor += contact.Hard ? 0 : 1;
    }

    private static string GetContactStateLabel(PhysicsComponent body)
    {
        var awake = body.Awake ? "awake" : "sleep";
        return $"{body.BodyType}/{body.BodyStatus}/{awake}";
    }

    private string GetPrototype(EntityUid uid)
    {
        return TryComp(uid, out MetaDataComponent? meta)
            ? meta.EntityPrototype?.ID ?? "<no-prototype>"
            : "<missing-meta>";
    }

    private string GetEntityName(EntityUid uid)
    {
        return TryComp(uid, out MetaDataComponent? meta)
            ? meta.EntityName
            : "<missing-meta>";
    }

    private static void AppendBodyLine(StringBuilder builder, BodyDiagnostics body)
    {
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"{body.Uid.Id,8} {body.Prototype} name=\"{body.Name}\" map={body.Map} grid={body.Grid} {body.BodyType}/{body.BodyStatus} collide={body.CanCollide} hard={body.Hard} sleep={body.SleepingAllowed} settled={body.Settled} ready={body.SleepReady} mass={body.Mass:0.###} speed={body.Speed:0.###} angular={body.AngularSpeed:0.###} sleepTime={body.SleepTime:0.###} contacts={body.Contacts} touching={body.TouchingContacts} hardContacts={body.HardContacts}");
    }

    private bool IsSettled(PhysicsComponent body)
    {
        return body.SleepingAllowed &&
               body.LinearVelocity.LengthSquared() <= LinearToleranceSqr &&
               body.AngularVelocity * body.AngularVelocity <= AngularToleranceSqr;
    }

    private sealed class BodyGroupDiagnostics(string prototype)
    {
        public readonly string Prototype = prototype;
        public int Count;
        public int Contacts;
        public int TouchingContacts;
        public int HardContacts;
        public int SleepingDisabled;
        public int InAir;
        public int ZeroContacts;
        public int ZeroContactSettled;
        public int SleepReady;
        public float MaxSleepTime;
        public float MaxSpeed;
    }

    private sealed class BodyStateDiagnostics(string label)
    {
        public readonly string Label = label;
        public int Count;
        public int Contacts;
        public int TouchingContacts;
        public int HardContacts;
        public int ZeroContacts;
        public int Settled;
        public int SleepReady;
        public float MaxSleepTime;
        public float MaxSpeed;
    }

    private sealed class ContactPairDiagnostics(string label)
    {
        public readonly string Label = label;
        public int Count;
        public int Touching;
        public int Hard;
        public int Sensor;
    }

    private sealed class ContactBodyDiagnostics(string prototype)
    {
        public readonly string Prototype = prototype;
        public readonly HashSet<EntityUid> Bodies = [];
        public int ContactRefs;
        public int TouchingRefs;
        public int HardRefs;
        public int AwakeRefs;
    }

    private readonly record struct BodyDiagnostics(
        EntityUid Uid,
        string Prototype,
        string Name,
        string Map,
        string Grid,
        string BodyType,
        string BodyStatus,
        bool CanCollide,
        bool Hard,
        bool SleepingAllowed,
        float Mass,
        float Speed,
        float AngularSpeed,
        float SleepTime,
        bool Settled,
        bool SleepReady,
        int Contacts,
        int TouchingContacts,
        int HardContacts);

    private readonly record struct ContactKey(int EntityA, int EntityB, string FixtureA, string FixtureB)
    {
        public static ContactKey Create(Contact contact)
        {
            if (contact.EntityA.Id <= contact.EntityB.Id)
                return new ContactKey(contact.EntityA.Id, contact.EntityB.Id, contact.FixtureAId, contact.FixtureBId);

            return new ContactKey(contact.EntityB.Id, contact.EntityA.Id, contact.FixtureBId, contact.FixtureAId);
        }
    }
}

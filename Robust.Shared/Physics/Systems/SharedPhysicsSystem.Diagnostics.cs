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
        var bodyLines = new List<BodyDiagnostics>(AwakeBodies.Count);
        var contactPairs = new Dictionary<string, ContactPairDiagnostics>(StringComparer.Ordinal);
        var seenContacts = new HashSet<ContactKey>();

        var hardContactCount = 0;
        var touchingContactCount = 0;
        var sleepingDisabledCount = 0;
        var inAirCount = 0;

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

            if (!body.SleepingAllowed)
                sleepingDisabledCount++;

            if (body.BodyStatus == BodyStatus.InAir)
                inAirCount++;

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
                    AddContactPair(contactPairs, contact);
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
            group.MaxSpeed = Math.Max(group.MaxSpeed, speed);

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
                contacts,
                touchingContacts,
                hardContacts));
        }

        var builder = new StringBuilder();
        builder.AppendLine("physics diagnostics");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"awake={bodyLines.Count}, hardContacts={hardContactCount}, touchingContactRefs={touchingContactCount}, movedGrids={MovedGrids.Count}, moveBuffer={MoveBuffer.Count}");
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"sleepingDisabled={sleepingDisabledCount}, inAir={inAirCount}, limit={limit}");

        builder.AppendLine();
        builder.AppendLine("top awake prototypes:");
        foreach (var group in bodyGroups.Values
                     .OrderByDescending(group => group.Count)
                     .ThenByDescending(group => group.HardContacts)
                     .ThenBy(group => group.Prototype, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{group.Count,5} {group.Prototype} contacts={group.Contacts} touching={group.TouchingContacts} hard={group.HardContacts} inAir={group.InAir} sleepOff={group.SleepingDisabled} maxSpeed={group.MaxSpeed:0.###}");
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
        foreach (var pair in contactPairs.Values
                     .OrderByDescending(pair => pair.Count)
                     .ThenBy(pair => pair.Label, StringComparer.Ordinal)
                     .Take(limit))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"{pair.Count,5} {pair.Label}");
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
            $"{body.Uid.Id,8} {body.Prototype} name=\"{body.Name}\" map={body.Map} grid={body.Grid} {body.BodyType}/{body.BodyStatus} collide={body.CanCollide} hard={body.Hard} sleep={body.SleepingAllowed} mass={body.Mass:0.###} speed={body.Speed:0.###} angular={body.AngularSpeed:0.###} contacts={body.Contacts} touching={body.TouchingContacts} hardContacts={body.HardContacts}");
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
        public float MaxSpeed;
    }

    private sealed class ContactPairDiagnostics(string label)
    {
        public readonly string Label = label;
        public int Count;
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

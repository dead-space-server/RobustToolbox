using System.Collections.Generic;
using JetBrains.Annotations;
using Robust.Server.Physics;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;

namespace Robust.Server.GameObjects
{
    [UsedImplicitly]
    public sealed class PhysicsSystem : SharedPhysicsSystem
    {
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;

        private readonly List<Entity<PhysicsComponent, TransformComponent>> _safetySleepBuffer = new();
        private EntityQuery<ActorComponent> _actorQuery;
        private EntityQuery<MapGridComponent> _gridQuery;
        private EntityQuery<JointComponent> _jointQuery;
        private EntityQuery<JointRelayTargetComponent> _jointRelayQuery;

        public override void Initialize()
        {
            base.Initialize();
            LoadMetricCVar();

            _actorQuery = GetEntityQuery<ActorComponent>();
            _gridQuery = GetEntityQuery<MapGridComponent>();
            _jointQuery = GetEntityQuery<JointComponent>();
            _jointRelayQuery = GetEntityQuery<JointRelayTargetComponent>();

            Subs.CVar(_configurationManager, CVars.MetricsEnabled, _ => LoadMetricCVar());
        }

        private void LoadMetricCVar()
        {
            MetricsEnabled = _configurationManager.GetCVar(CVars.MetricsEnabled);
        }

        /// <inheritdoc />
        public override void Update(float frameTime)
        {
            SimulateWorld(frameTime, false);
        }

        protected override void Cleanup(float frameTime)
        {
            base.Cleanup(frameTime);
            SleepIdleDetachedBodies(frameTime);
        }

        private void SleepIdleDetachedBodies(float frameTime)
        {
            if (frameTime <= 0f)
                return;

            if (AwakeBodies.Count == 0)
                return;

            _safetySleepBuffer.AddRange(AwakeBodies);

            foreach (var ent in _safetySleepBuffer)
            {
                var body = ent.Comp1;

                if (!CanSafetySleep(ent.Owner, body, ent.Comp2))
                {
                    continue;
                }

                var sleepReady = body.SleepTime >= TimeToSleep;

                if (!sleepReady)
                {
                    if (body.ContactCount != 0)
                        continue;

                    SetSleepTime(body, body.SleepTime + frameTime);
                    sleepReady = body.SleepTime >= TimeToSleep;
                }

                if (sleepReady)
                    SetAwake(ent, false);
            }

            _safetySleepBuffer.Clear();
        }

        private bool CanSafetySleep(EntityUid uid, PhysicsComponent body, TransformComponent xform)
        {
            if (!body.Awake ||
                !body.CanCollide ||
                !body.SleepingAllowed ||
                xform.MapUid == null ||
                _actorQuery.HasComponent(uid) ||
                _gridQuery.HasComponent(uid) ||
                HasJoints(uid))
            {
                return false;
            }

            if (body.BodyType != BodyType.Dynamic &&
                body.BodyType != BodyType.KinematicController)
            {
                return false;
            }

            if (body.BodyType == BodyType.Dynamic &&
                body.BodyStatus == BodyStatus.InAir &&
                xform.GridUid != null)
            {
                return false;
            }

            if (body.LinearVelocity.LengthSquared() > LinearToleranceSqr ||
                body.AngularVelocity * body.AngularVelocity > AngularToleranceSqr)
            {
                return false;
            }

            return body.ContactCount == 0 ||
                   body.SleepTime >= TimeToSleep;
        }

        private bool HasJoints(EntityUid uid)
        {
            if (_jointQuery.TryGetComponent(uid, out var joints) &&
                (joints.Relay != null || joints.GetJoints.Count != 0))
            {
                return true;
            }

            return _jointRelayQuery.TryGetComponent(uid, out var relay) &&
                   relay.Relayed.Count != 0;
        }

    }
}

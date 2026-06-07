using System.Collections.Generic;
using JetBrains.Annotations;
using Robust.Server.Physics;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Robust.Server.GameObjects
{
    [UsedImplicitly]
    public sealed class PhysicsSystem : SharedPhysicsSystem
    {
        [Dependency] private readonly IConfigurationManager _configurationManager = default!;

        private readonly List<Entity<PhysicsComponent, TransformComponent>> _safetySleepBuffer = new();
        private readonly List<EntityUid> _staleSafetySleepBuffer = new();
        private readonly Dictionary<EntityUid, float> _safetySleepTimes = new();
        private EntityQuery<JointComponent> _jointQuery;
        private EntityQuery<JointRelayTargetComponent> _jointRelayQuery;

        public override void Initialize()
        {
            base.Initialize();
            LoadMetricCVar();

            _jointQuery = GetEntityQuery<JointComponent>();
            _jointRelayQuery = GetEntityQuery<JointRelayTargetComponent>();

            SubscribeLocalEvent<PhysicsComponent, PhysicsSleepEvent>(OnPhysicsSleepWake);
            SubscribeLocalEvent<PhysicsComponent, PhysicsWakeEvent>(OnPhysicsSleepWake);

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
            {
                _safetySleepTimes.Clear();
                return;
            }

            _safetySleepBuffer.AddRange(AwakeBodies);

            foreach (var ent in _safetySleepBuffer)
            {
                var body = ent.Comp1;

                if (!CanSafetySleep(ent.Owner, body, ent.Comp2))
                {
                    _safetySleepTimes.Remove(ent.Owner);
                    continue;
                }

                var sleepTime = _safetySleepTimes.GetValueOrDefault(ent.Owner) + frameTime;

                if (sleepTime >= TimeToSleep)
                    SetAwake(ent, false);
                else
                    _safetySleepTimes[ent.Owner] = sleepTime;
            }

            _safetySleepBuffer.Clear();
            PruneSafetySleepTimes();
        }

        private bool CanSafetySleep(EntityUid uid, PhysicsComponent body, TransformComponent xform)
        {
            return body.Awake &&
                   body.BodyType == BodyType.Dynamic &&
                   body.BodyStatus == BodyStatus.OnGround &&
                   body.CanCollide &&
                   body.SleepingAllowed &&
                   body.ContactCount == 0 &&
                   xform.MapUid != null &&
                   body.LinearVelocity.LengthSquared() <= LinearToleranceSqr &&
                   body.AngularVelocity * body.AngularVelocity <= AngularToleranceSqr &&
                   !HasJoints(uid);
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

        private void PruneSafetySleepTimes()
        {
            if (_safetySleepTimes.Count <= AwakeBodies.Count + 128)
                return;

            foreach (var (uid, _) in _safetySleepTimes)
            {
                if (!PhysicsQuery.TryGetComponent(uid, out var body) || !body.Awake)
                    _staleSafetySleepBuffer.Add(uid);
            }

            foreach (var uid in _staleSafetySleepBuffer)
            {
                _safetySleepTimes.Remove(uid);
            }

            _staleSafetySleepBuffer.Clear();
        }

        private void OnPhysicsSleepWake(EntityUid uid, PhysicsComponent component, ref PhysicsSleepEvent args)
        {
            _safetySleepTimes.Remove(uid);
        }

        private void OnPhysicsSleepWake(EntityUid uid, PhysicsComponent component, ref PhysicsWakeEvent args)
        {
            _safetySleepTimes.Remove(uid);
        }
    }
}

using Content.Shared.Projectiles;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;

namespace Content.Shared._RMC14.Explosion;

public sealed class ProjectileNetworkSafetySystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        // Use multiple approaches to ensure we catch all cases
        SubscribeLocalEvent<ProjectileComponent, ComponentGetStateAttemptEvent>(OnGetProjectileStateAttempt);
        SubscribeLocalEvent<ProjectileComponent, ComponentGetState>(OnGetProjectileState);

        SubscribeLocalEvent<ProjectileLimitHitsComponent, ComponentGetStateAttemptEvent>(OnGetProjectileLimitStateAttempt);
        SubscribeLocalEvent<ProjectileLimitHitsComponent, ComponentGetState>(OnGetProjectileLimitState);
    }

    private void OnGetProjectileStateAttempt(EntityUid uid, ProjectileComponent component, ref ComponentGetStateAttemptEvent args)
    {
        if (!IsEntityValidForNetwork(uid))
        {
            args.Cancelled = true;
        }
    }

    private void OnGetProjectileState(EntityUid uid, ProjectileComponent component, ref ComponentGetState args)
    {
        if (!IsEntityValidForNetwork(uid))
        {
            args.State = null;
        }
    }

    private void OnGetProjectileLimitStateAttempt(EntityUid uid, ProjectileLimitHitsComponent component, ref ComponentGetStateAttemptEvent args)
    {
        if (!IsEntityValidForNetwork(uid))
        {
            args.Cancelled = true;
        }
    }

    private void OnGetProjectileLimitState(EntityUid uid, ProjectileLimitHitsComponent component, ref ComponentGetState args)
    {
        if (!IsEntityValidForNetwork(uid))
        {
            args.State = null;
        }
    }

    /// <summary>
    /// Comprehensive validation for entities that will be used in network operations
    /// </summary>
    private bool IsEntityValidForNetwork(EntityUid uid)
    {
        return !Deleted(uid) &&
               !Terminating(uid) &&
               EntityManager.EntityExists(uid) &&
               HasComp<MetaDataComponent>(uid) &&
               HasComp<TransformComponent>(uid);
    }
}

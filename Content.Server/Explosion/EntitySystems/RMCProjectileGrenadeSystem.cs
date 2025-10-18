using Content.Server.Explosion.Components;
using Content.Shared._RMC14.Armor;
using Content.Shared._RMC14.Explosion;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Explosion.EntitySystems;

public sealed class RMCProjectileGrenadeSystem : EntitySystem
{
    private readonly List<EntityUid> _hitEntities = new();

    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly GunIFFSystem _gunIFF = default!;
    [Dependency] private readonly EntityLookupSystem _entityLookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProjectileGrenadeComponent, ProjectileHitEvent>(OnStartCollide);
        SubscribeLocalEvent<ProjectileGrenadeComponent, FragmentIntoProjectilesEvent>(OnFragmentIntoProjectiles);

        // Staggered spawning subscriptions
        SubscribeLocalEvent<RMCStaggeredGrenadeComponent, TriggerEvent>(OnStaggeredGrenadeTriggered);
    }

    /// <summary>
    /// Reverses the payload shooting direction if the projectile grenade collides with an entity
    /// </summary>
    private void OnStartCollide(Entity<ProjectileGrenadeComponent> ent, ref ProjectileHitEvent args)
    {
        if (!ent.Comp.Rebounds)
            return;

        var reboundTimer = EnsureComp<ActiveTimerTriggerComponent>(ent);
        reboundTimer.TimeRemaining = ent.Comp.ReboundTimer;

        var ev = new ActiveTimerTriggerEvent(ent, args.Shooter);
        RaiseLocalEvent(ent, ref ev);
    }

    /// <summary>
    /// Overwrites the logic of the upstream <seealso cref="ProjectileGrenadeSystem"/> to allow more customization
    /// </summary>
    private void OnFragmentIntoProjectiles(Entity<ProjectileGrenadeComponent> ent, ref FragmentIntoProjectilesEvent args)
    {
        if (ent.Comp.DirectHit && args.ShootCount == 0)
        {
            _hitEntities.Clear();
            var directHit = DirectHit(ent, args.ContentUid, args.TotalCount);
            if (directHit != null)
            {
                args.HitEntities = _hitEntities;
                args.TotalCount = directHit.Value;
            }
        }

        args.Handled = true;
        var segmentAngle = ent.Comp.SpreadAngle / args.TotalCount;
        var projectileRotation = _transform.GetMoverCoordinateRotation(ent.Owner, Transform(ent.Owner)).worldRot.Degrees + ent.Comp.DirectionAngle;

        // Give the same IFF faction and enabled state to the projectiles shot from the grenade
        if (ent.Comp.InheritIFF)
        {
            if (TryComp(ent.Owner, out ProjectileIFFComponent? grenadeIFFComponent))
            {
                _gunIFF.GiveAmmoIFF(args.ContentUid, grenadeIFFComponent.Faction, grenadeIFFComponent.Enabled);
            }
        }

        var angleMin = projectileRotation - ent.Comp.SpreadAngle / 2 + segmentAngle * args.ShootCount;
        var angleMax = projectileRotation - ent.Comp.SpreadAngle / 2 + segmentAngle * (args.ShootCount + 1);

        if (ent.Comp.EvenSpread)
            args.Angle = Angle.FromDegrees((angleMin + angleMax) / 2);
        else
            args.Angle = Angle.FromDegrees(_random.Next((int)angleMin, (int)angleMax));
    }

    // Directly hit any entities close enough to the grenade.
    private int? DirectHit(Entity<ProjectileGrenadeComponent> ent, EntityUid payloadUid,  int projectileCount)
    {
        if (!TryComp(payloadUid, out ProjectileComponent? projectile))
            return null;

        var nearbyEntities = _entityLookup.GetEntitiesInRange<MobStateComponent>(Transform(ent).Coordinates, 0.5f);
        var armorPiercing = 0;

        foreach (var entity in nearbyEntities)
        {
            if (_mobState.IsDead(entity))
                continue;

            // Deal damage directly and remove projectiles from the grenade
            var newProjectileCount = projectileCount - ent.Comp.DirectHitProjectiles;
            var damage = projectile.Damage * ent.Comp.DirectHitProjectiles;
            if (newProjectileCount < 0)
                damage += projectile.Damage * newProjectileCount;

            if (TryComp(payloadUid, out CMArmorPiercingComponent? armorPiercingComp))
                armorPiercing = armorPiercingComp.Amount;

            projectileCount = Math.Max(newProjectileCount, 0);
            _damage.TryChangeDamage(entity, damage, armorPiercing: armorPiercing);

            // Make sure the leftover projectiles don't hit the entity that was hit directly
            if (!TryComp(entity, out UserLimitHitsComponent? limit))
                continue;

            _hitEntities.Add(entity);
            limit.HitBy.Add(new Hit(GetNetEntity(ent.Owner), _timing.CurTime + limit.Expire, null));
            Dirty(entity,limit);

            if(projectileCount == 0)
                break;
        }

        return projectileCount;
    }

    /// <summary>
    /// Handles staggered grenade triggering - prevents immediate spawning and starts staggered process
    /// </summary>
    private void OnStaggeredGrenadeTriggered(Entity<RMCStaggeredGrenadeComponent> ent, ref TriggerEvent args)
    {
        if (!TryComp<ProjectileGrenadeComponent>(ent, out var grenade) ||
            !TryComp<StaggeredSpawnDetailsComponent>(ent, out var staggerDetails))
        {
            return;
        }

        // Start staggered spawning instead of immediate spawning
        StartStaggeredSpawning(ent, grenade, staggerDetails);

        // Prevent the original grenade system from handling this
        args.Handled = true;
    }

    /// <summary>
    /// Starts the staggered spawning process for performance optimization
    /// </summary>
    private void StartStaggeredSpawning(EntityUid uid, ProjectileGrenadeComponent grenade, StaggeredSpawnDetailsComponent staggerDetails)
    {
        // Calculate how many ticks we need
        var totalProjectiles = grenade.Capacity;
        var spawnsPerTick = staggerDetails.SpawnsPerTick;
        var totalTicks = (int)Math.Ceiling((double)totalProjectiles / spawnsPerTick);

        // Start the staggered spawning process
        var state = new StaggeredSpawnComponent
        {
            TotalProjectiles = totalProjectiles,
            SpawnsPerTick = spawnsPerTick,
            CurrentTick = 0,
            TotalTicks = totalTicks,
            Prototype = grenade.FillPrototype!
        };

        EnsureComp<ActiveStaggeredSpawnerComponent>(uid);
        AddComp(uid, state);

        // No Dirty() call needed - these are server-only components
    }

    /// <summary>
    /// Processes staggered spawning for performance-optimized grenades
    /// </summary>
    private void ProcessStaggeredSpawn(Entity<StaggeredSpawnComponent> ent)
    {
        var remainingProjectiles = ent.Comp.TotalProjectiles - (ent.Comp.CurrentTick * ent.Comp.SpawnsPerTick);
        var projectilesThisTick = Math.Min(ent.Comp.SpawnsPerTick, remainingProjectiles);

        if (projectilesThisTick <= 0)
        {
            // Finished spawning
            RemComp<ActiveStaggeredSpawnerComponent>(ent);
            RemComp<StaggeredSpawnComponent>(ent);
            return;
        }

        // Spawn projectiles for this tick
        for (int i = 0; i < projectilesThisTick; i++)
        {
            var projectile = Spawn(ent.Comp.Prototype, Transform(ent).Coordinates);

            // Apply random velocity
            var direction = _random.NextAngle().ToVec();
            if (TryComp<PhysicsComponent>(projectile, out var physics))
            {
                _physics.ApplyLinearImpulse(projectile, direction * _random.Next(5, 10), body: physics);
            }

            // Give the same IFF faction and enabled state
            if (TryComp(ent.Owner, out ProjectileIFFComponent? grenadeIFFComponent))
            {
                _gunIFF.GiveAmmoIFF(projectile, grenadeIFFComponent.Faction, grenadeIFFComponent.Enabled);
            }
        }

        ent.Comp.CurrentTick++;
        // No Dirty() call needed - these are server-only components
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        // Your existing physics update
        var query = EntityQueryEnumerator<ProjectileGrenadeComponent, PhysicsComponent>();
        while (query.MoveNext(out var projectileUid, out _, out var physics))
        {
            _transform.SetWorldRotationNoLerp(projectileUid, physics.LinearVelocity.ToWorldAngle());
        }

        // Add staggered spawning processing
        var staggerQuery = EntityQueryEnumerator<ActiveStaggeredSpawnerComponent, StaggeredSpawnComponent>();
        while (staggerQuery.MoveNext(out var uid, out var _, out var state))
        {
            ProcessStaggeredSpawn((uid, state));
        }
    }
}

/// <summary>
///     Raised when a projectile grenade is being triggered
/// </summary>
[ByRefEvent]
public record struct FragmentIntoProjectilesEvent(EntityUid ContentUid, int TotalCount, Angle Angle, int ShootCount, List<EntityUid> HitEntities, bool Handled = false);

/// <summary>
/// Marker component for grenades that use the staggered spawning system
/// </summary>
[RegisterComponent]
public sealed partial class RMCStaggeredGrenadeComponent : Component
{
}

/// <summary>
/// Configuration for staggered spawning behavior
/// </summary>
[RegisterComponent]
public sealed partial class StaggeredSpawnDetailsComponent : Component
{
    [DataField("spawnsPerTick")]
    public int SpawnsPerTick = 10;
}

/// <summary>
/// Active component indicating a grenade is currently in the process of staggered spawning
/// </summary>
[RegisterComponent]
public sealed partial class ActiveStaggeredSpawnerComponent : Component
{
}

/// <summary>
/// Tracks the current state of staggered spawning
/// </summary>
[RegisterComponent]
public sealed partial class StaggeredSpawnComponent : Component
{
    [DataField("totalProjectiles")]
    public int TotalProjectiles;

    [DataField("spawnsPerTick")]
    public int SpawnsPerTick;

    [DataField("currentTick")]
    public int CurrentTick;

    [DataField("totalTicks")]
    public int TotalTicks;

    [DataField("prototype")]
    public string Prototype = string.Empty;
}

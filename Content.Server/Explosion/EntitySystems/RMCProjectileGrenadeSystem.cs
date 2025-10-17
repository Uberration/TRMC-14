using Content.Server.Explosion.Components;
using Content.Shared._RMC14.Armor;
using Content.Shared._RMC14.Explosion;
using Content.Shared._RMC14.Weapons.Ranged.IFF;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Projectiles;
using Robust.Server.GameObjects;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.Physics.Systems;

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
        SubscribeLocalEvent<ProjectileGrenadeComponent, ComponentShutdown>(OnShutdown);
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
    private void OnShutdown(Entity<ProjectileGrenadeComponent> ent, ref ComponentShutdown args)
    {
        // Clean up if the grenade is deleted before finishing its fragmentation
        if (ent.Comp.TotalToSpawn > 0)
        {
            ent.Comp.TotalToSpawn = 0;
            ent.Comp.SpawnedCount = 0;
        }
    }

    /// <summary>
    /// Overwrites the logic of the upstream <seealso cref="ProjectileGrenadeSystem"/> to allow more customization
    /// </summary>
    private void OnFragmentIntoProjectiles(Entity<ProjectileGrenadeComponent> ent, ref FragmentIntoProjectilesEvent args)
    {
        args.Handled = true;

        var totalCount = args.TotalCount;
        if (ent.Comp.DirectHit && args.ShootCount == 0)
        {
            _hitEntities.Clear();
            var directHit = DirectHit(ent, args.ContentUid, totalCount);
            if (directHit != null)
            {
                args.HitEntities = _hitEntities;
                totalCount = directHit.Value;
            }
        }

        if (totalCount <= 0)
        {
            // Nothing to spawn, we are done.
            return;
        }

        // If MaxProjectilesPerTick is set to > 0, we start the staggered spawning process.
        if (ent.Comp.MaxProjectilesPerTick > 0)
        {
            ent.Comp.TotalToSpawn = totalCount;
            ent.Comp.SpawnedCount = 0;
            // The remaining logic will be handled in the Update loop.
            return;
        }

        // Legacy behavior: spawn all at once.
        SpawnProjectilesBatch(ent, args.ContentUid, totalCount, args.ShootCount, ref args.Angle);
    }

    private void SpawnProjectilesBatch(Entity<ProjectileGrenadeComponent> ent, EntityUid contentUid, int count, int shootCount, ref Angle angle)
    {
        // The original logic used args.TotalCount which is the total capacity.
        // We need to use ent.Comp.Capacity for the segmentAngle calculation if we want to maintain the spread.
        var segmentAngle = ent.Comp.SpreadAngle / ent.Comp.Capacity;
        var projectileRotation = _transform.GetMoverCoordinateRotation(ent.Owner, Transform(ent.Owner)).worldRot.Degrees + ent.Comp.DirectionAngle;

        // Give the same IFF faction and enabled state to the projectiles shot from the grenade
        if (ent.Comp.InheritIFF)
        {
            if (TryComp(ent.Owner, out ProjectileIFFComponent? grenadeIFFComponent))
            {
                _gunIFF.GiveAmmoIFF(contentUid, grenadeIFFComponent.Faction, grenadeIFFComponent.Enabled);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var currentShootCount = shootCount + i;
            var angleMin = projectileRotation - ent.Comp.SpreadAngle / 2 + segmentAngle * currentShootCount;
            var angleMax = projectileRotation - ent.Comp.SpreadAngle / 2 + segmentAngle * (currentShootCount + 1);

            if (ent.Comp.EvenSpread)
                angle = Angle.FromDegrees((angleMin + angleMax) / 2);
            else
                angle = Angle.FromDegrees(_random.Next((int)angleMin, (int)angleMax));

            // The actual projectile spawning logic is handled by the upstream ProjectileGrenadeSystem.
            // We need to trigger the event that causes the spawning.
            var ev = new FragmentIntoProjectilesEvent(contentUid, ent.Comp.Capacity, angle, currentShootCount, _hitEntities, false);
            RaiseLocalEvent(ent, ref ev);
        }
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

    public override void Update(float frametime)
    {
        var query = EntityQueryEnumerator<ProjectileGrenadeComponent, PhysicsComponent>();
        while (query.MoveNext(out var projectileUid, out var comp, out var physics))
        {
            _transform.SetWorldRotationNoLerp(projectileUid, physics.LinearVelocity.ToWorldAngle());

            // Handle staggered spawning
            if (comp.TotalToSpawn > 0 && comp.Container.ContainedEntity.HasValue)
            {
                var remaining = comp.TotalToSpawn - comp.SpawnedCount;
                var toSpawn = Math.Min(remaining, comp.MaxProjectilesPerTick);

                if (toSpawn > 0)
                {
                    var angle = Angle.Zero; // Angle is calculated inside SpawnProjectilesBatch
                    SpawnProjectilesBatch((projectileUid, comp), comp.Container.ContainedEntity.Value, toSpawn, comp.SpawnedCount, ref angle);
                    comp.SpawnedCount += toSpawn;
                }

                if (comp.SpawnedCount >= comp.TotalToSpawn)
                {
                    // Finished spawning, clean up state
                    comp.TotalToSpawn = 0;
                    comp.SpawnedCount = 0;
                }
            }
        }
    }
}

/// <summary>
///     Raised when a projectile grenade is being triggered
/// </summary>
[ByRefEvent]

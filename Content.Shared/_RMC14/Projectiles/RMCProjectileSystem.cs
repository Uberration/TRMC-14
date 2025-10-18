using System.Numerics;
using Content.Shared._RMC14.Evasion;
using Content.Shared._RMC14.Random;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared.Examine;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Systems;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Whitelist;
using Robust.Shared.Network;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Timing;

namespace Content.Shared._RMC14.Projectiles;

public sealed class RMCProjectileSystem : EntitySystem
{
    [Dependency] private readonly ExamineSystemShared _examine = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NpcFactionSystem _npcFaction = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedXenoHiveSystem _hive = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<DeleteOnCollideComponent, StartCollideEvent>(OnDeleteOnCollideStartCollide);
        SubscribeLocalEvent<ModifyTargetOnHitComponent, ProjectileHitEvent>(OnModifyTargetOnHit);
        SubscribeLocalEvent<ProjectileMaxRangeComponent, MapInitEvent>(OnProjectileMaxRangeMapInit);

        SubscribeLocalEvent<RMCProjectileDamageFalloffComponent, MapInitEvent>(OnFalloffProjectileMapInit);
        SubscribeLocalEvent<RMCProjectileDamageFalloffComponent, ProjectileHitEvent>(OnFalloffProjectileHit);

        SubscribeLocalEvent<RMCProjectileAccuracyComponent, MapInitEvent>(OnProjectileAccuracyMapInit);
        SubscribeLocalEvent<RMCProjectileAccuracyComponent, PreventCollideEvent>(OnProjectileAccuracyPreventCollide);

        SubscribeLocalEvent<SpawnOnTerminateComponent, MapInitEvent>(OnSpawnOnTerminatingMapInit);
        SubscribeLocalEvent<SpawnOnTerminateComponent, EntityTerminatingEvent>(OnSpawnOnTerminatingTerminate);

        SubscribeLocalEvent<PreventCollideWithDeadComponent, PreventCollideEvent>(OnPreventCollideWithDead);
    }

    private void OnDeleteOnCollideStartCollide(Entity<DeleteOnCollideComponent> ent, ref StartCollideEvent args)
    {
        // Validate both entities before processing
        if (!IsValidAndAlive(ent) || !IsValidAndAlive(args.OtherEntity))
            return;

        if (_net.IsServer && IsValidAndAlive(ent))
            QueueDel(ent);
    }

    private void OnModifyTargetOnHit(Entity<ModifyTargetOnHitComponent> ent, ref ProjectileHitEvent args)
    {
        // Validate entities before processing
        if (!IsValidAndAlive(ent) || !IsValidAndAlive(args.Target))
            return;

        if (!_whitelist.IsWhitelistPassOrNull(ent.Comp.Whitelist, args.Target))
            return;

        // Only add components if target is still valid
        if (ent.Comp.Add is { } add && IsValidAndAlive(args.Target))
            EntityManager.AddComponents(args.Target, add);
    }

    private void OnProjectileMaxRangeMapInit(Entity<ProjectileMaxRangeComponent> ent, ref MapInitEvent args)
    {
        if (!IsValidAndAlive(ent))
            return;

        ent.Comp.Origin = _transform.GetMoverCoordinates(ent);
        Dirty(ent);
    }

    private void OnFalloffProjectileMapInit(Entity<RMCProjectileDamageFalloffComponent> projectile, ref MapInitEvent args)
    {
        if (!IsValidAndAlive(projectile))
            return;

        projectile.Comp.ShotFrom = _transform.GetMoverCoordinates(projectile.Owner);
        Dirty(projectile);
    }

    private void OnFalloffProjectileHit(Entity<RMCProjectileDamageFalloffComponent> projectile, ref ProjectileHitEvent args)
    {
        // Validate all entities before processing
        if (!IsValidAndAlive(projectile) || !IsValidAndAlive(args.Target))
            return;

        if (projectile.Comp.ShotFrom == null || projectile.Comp.MinRemainingDamageMult < 0)
            return;

        var targetCoords = _transform.GetMoverCoordinates(args.Target);
        var distance = (targetCoords.Position - projectile.Comp.ShotFrom.Value.Position).Length();
        var minDamage = args.Damage.GetTotal() * projectile.Comp.MinRemainingDamageMult;

        foreach (var threshold in projectile.Comp.Thresholds)
        {
            var pastEffectiveRange = distance - threshold.Range;

            if (pastEffectiveRange <= 0)
                continue;

            var totalDamage = args.Damage.GetTotal();
            if (totalDamage <= minDamage)
                break;

            var extraMult = threshold.IgnoreModifiers ? 1 : projectile.Comp.WeaponMult;
            var minMult = FixedPoint2.Min(minDamage / totalDamage, 1);

            args.Damage *= FixedPoint2.Clamp((totalDamage - pastEffectiveRange * threshold.Falloff * extraMult) / totalDamage, minMult, 1);
        }
    }

    public void SetProjectileFalloffWeaponMult(Entity<RMCProjectileDamageFalloffComponent> projectile, FixedPoint2 mult, float range)
    {
        if (!IsValidAndAlive(projectile))
            return;

        var count = 0;
        while (projectile.Comp.Thresholds.Count > count)
        {
            var threshold = projectile.Comp.Thresholds[count];
            projectile.Comp.Thresholds[count] = threshold with { Range = threshold.Range + range };
            count++;
        }

        projectile.Comp.WeaponMult = mult;
        Dirty(projectile);
    }

    private void OnProjectileAccuracyMapInit(Entity<RMCProjectileAccuracyComponent> projectile, ref MapInitEvent args)
    {
        if (!IsValidAndAlive(projectile))
            return;

        projectile.Comp.ShotFrom = _transform.GetMoverCoordinates(projectile.Owner);
        projectile.Comp.Tick = _timing.CurTick.Value;

        Dirty(projectile);
    }

    private void OnProjectileAccuracyPreventCollide(Entity<RMCProjectileAccuracyComponent> projectile, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        // Validate entities before processing
        if (!IsValidAndAlive(projectile) || !IsValidAndAlive(args.OtherEntity))
            return;

        if (projectile.Comp.ForceHit || projectile.Comp.ShotFrom == null)
            return;

        if (!TryComp(projectile.Owner, out ProjectileComponent? projectileComponent) || projectileComponent.Shooter == null)
            return;

        if (!TryComp(args.OtherEntity, out EvasionComponent? evasionComponent))
            return;

        var accuracy = projectile.Comp.Accuracy;
        var targetCoords = _transform.GetMoverCoordinates(args.OtherEntity);
        var distance = (targetCoords.Position - projectile.Comp.ShotFrom.Value.Position).Length();

        foreach (var threshold in projectile.Comp.Thresholds)
        {
            var pastRange = distance - threshold.Range;

            if (threshold.Buildup)
            {
                if (pastRange >= 0)
                    continue;

                accuracy += threshold.Falloff * pastRange;
                continue;
            }

            if (pastRange <= 0)
                continue;

            accuracy -= threshold.Falloff * pastRange;
        }

        if (!_examine.InRangeUnOccluded(_transform.ToMapCoordinates(projectile.Comp.ShotFrom.Value), _transform.ToMapCoordinates(targetCoords), distance, null))
            accuracy += (int) AccuracyModifiers.TargetOccluded;

        if (!projectile.Comp.IgnoreFriendlyEvasion && IsProjectileTargetFriendly(projectile.Owner, args.OtherEntity))
            accuracy -= evasionComponent.ModifiedEvasionFriendly;

        accuracy -= evasionComponent.ModifiedEvasion;

        accuracy = accuracy > projectile.Comp.MinAccuracy ? accuracy : projectile.Comp.MinAccuracy;

        // Fix: Ensure GunSeed and Tick are properly initialized
        var gunSeed = projectile.Comp.GunSeed;
        var tick = projectile.Comp.Tick;

        // Use the entity's hash code for the random seed
        var entityHash = args.OtherEntity.GetHashCode();
        var randomSeed = (long) tick << 32 | (uint)entityHash;
        var random = new Xoshiro128P(gunSeed, randomSeed).NextFloat(0f, 100f);

        if (accuracy >= random)
            return;

        args.Cancelled = true;
    }

    private bool IsProjectileTargetFriendly(EntityUid projectile, EntityUid target)
    {
        if (!IsValidAndAlive(projectile) || !IsValidAndAlive(target))
            return false;

        if (!TryComp(projectile, out ProjectileComponent? projectileComp) || projectileComp.Shooter == null)
            return false;

        // Check if shooter is still valid
        if (!IsValidAndAlive(projectileComp.Shooter.Value))
            return false;

        return _npcFaction.IsEntityFriendly(projectileComp.Shooter.Value, target);
    }

    private void OnSpawnOnTerminatingMapInit(Entity<SpawnOnTerminateComponent> ent, ref MapInitEvent args)
    {
        if (!IsValidAndAlive(ent))
            return;

        ent.Comp.Origin = _transform.GetMoverCoordinates(ent);
        Dirty(ent);
    }

    private void OnSpawnOnTerminatingTerminate(Entity<SpawnOnTerminateComponent> ent, ref EntityTerminatingEvent args)
    {
        if (_net.IsClient)
            return;

        // Don't process if entity is already being terminated
        if (TerminatingOrDeleted(ent))
            return;

        if (!TryComp(ent, out TransformComponent? transform))
            return;

        if (TerminatingOrDeleted(transform.ParentUid))
            return;

        var coordinates = transform.Coordinates;
        if (ent.Comp.ProjectileAdjust &&
            ent.Comp.Origin is { } origin &&
            coordinates.TryDelta(EntityManager, _transform, origin, out var delta) &&
            delta.Length() > 0)
        {
            coordinates = coordinates.Offset(delta.Normalized() / -2);

            if (HasComp<RMCFireProjectileComponent>(ent))
            {
                coordinates = coordinates.Offset(delta.Normalized());
            }
        }

        var spawn = SpawnAtPosition(ent.Comp.Spawn, coordinates);
        _hive.SetSameHive(ent.Owner, spawn);

        if (ent.Comp.Popup is { } popup)
            _popup.PopupCoordinates(Loc.GetString(popup), coordinates, ent.Comp.PopupType ?? PopupType.Small);
    }

    private void OnPreventCollideWithDead(Entity<PreventCollideWithDeadComponent> ent, ref PreventCollideEvent args)
    {
        if (args.Cancelled)
            return;

        // Validate entities before processing
        if (!IsValidAndAlive(ent) || !IsValidAndAlive(args.OtherEntity))
            return;

        if (_mobState.IsDead(args.OtherEntity))
            args.Cancelled = true;
    }

    public void SetMaxRange(Entity<ProjectileMaxRangeComponent> ent, float max)
    {
        if (!IsValidAndAlive(ent))
            return;

        ent.Comp.Max = max;
        Dirty(ent);
    }

    private void StopProjectile(Entity<ProjectileMaxRangeComponent> ent)
    {
        if (!IsValidAndAlive(ent))
            return;

        if (ent.Comp.Delete)
        {
            if (_net.IsServer || IsClientSide(ent))
                QueueDel(ent);
        }
        else
        {
            _physics.SetLinearVelocity(ent, Vector2.Zero);
            RemCompDeferred<ProjectileMaxRangeComponent>(ent);
        }
    }

    public override void Update(float frameTime)
    {
        var maxQuery = EntityQueryEnumerator<ProjectileMaxRangeComponent>();
        while (maxQuery.MoveNext(out var uid, out var comp))
        {
            // Check if entity is still valid before processing
            if (!IsValidAndAlive(uid))
                continue;

            var coordinates = _transform.GetMoverCoordinates(uid);
            if (comp.Origin is not { } origin ||
                !coordinates.TryDistance(EntityManager, _transform, origin, out var distance))
            {
                StopProjectile((uid, comp));
                continue;
            }

            if (distance < comp.Max && Math.Abs(distance - comp.Max) > 0.1f)
                continue;

            StopProjectile((uid, comp));
        }
    }

    /// <summary>
    /// Comprehensive check to ensure an entity is valid, not deleted, and has required components
    /// </summary>
    private bool IsValidAndAlive(EntityUid entity)
    {
        // Check if entity is valid and not deleted
        if (!Exists(entity) || Deleted(entity) || Terminating(entity))
            return false;

        // Ensure entity still has its metadata component and transform component
        // TransformComponent is essential for most entity operations
        return TryComp<MetaDataComponent>(entity, out var meta) &&
               !meta.EntityDeleted &&
               HasComp<TransformComponent>(entity);
    }

    /// <summary>
    /// Overload for Entity<T> that includes component validation
    /// </summary>
    private bool IsValidAndAlive<T>(Entity<T> entity) where T : IComponent
    {
        if (!IsValidAndAlive(entity.Owner))
            return false;

        // Use HasComp instead of TryComp to avoid null reference issues
        return HasComp<T>(entity.Owner);
    }
}

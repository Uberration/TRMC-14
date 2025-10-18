using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Robust.Shared.Physics.Events;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Shared.Explosion.EntitySystems;

public sealed class SharedRMCProjectileDamageSystem : EntitySystem
{
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly EntityLookupSystem _entityLookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IGameTiming _gameTiming = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RMCProjectileDamageComponent, StartCollideEvent>(OnProjectileCollide);
    }

    private void OnProjectileCollide(Entity<RMCProjectileDamageComponent> ent, ref StartCollideEvent args)
    {
        if (!args.OtherFixture.Hard)
            return;

        if (!_gameTiming.IsFirstTimePredicted)
            return;

        if (Deleted(ent) || Terminating(ent))
            return;

        ApplyProjectileDamage(ent, args.OtherEntity);

        // Immediately remove the entity to prevent any network sync
        // This is more aggressive but should prevent the errors
        Del(ent);
    }

    private void ApplyProjectileDamage(Entity<RMCProjectileDamageComponent> projectile, EntityUid target)
    {
        // Apply direct damage to the hit entity
        _damageable.TryChangeDamage(target, projectile.Comp.Damage, origin: projectile);

        // Apply area damage if radius > 0
        if (projectile.Comp.Radius > 0)
        {
            ApplyAreaDamage(projectile, _transform.GetMoverCoordinates(projectile));
        }
    }

    private void ApplyAreaDamage(Entity<RMCProjectileDamageComponent> projectile, EntityCoordinates coordinates)
    {
        var radius = projectile.Comp.Radius;
        var damage = projectile.Comp.Damage;

        // Find all damageable entities in radius
        var entities = _entityLookup.GetEntitiesInRange(coordinates, radius);

        foreach (var entity in entities)
        {
            if (entity == projectile.Owner)
                continue;

            _damageable.TryChangeDamage(entity, damage, origin: projectile);
        }
    }
}

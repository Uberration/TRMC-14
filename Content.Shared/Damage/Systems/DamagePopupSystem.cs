using Content.Shared.Damage.Components;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Robust.Shared.GameStates;

namespace Content.Shared.Damage.Systems;

public sealed class DamagePopupSystem : EntitySystem
{
    [Dependency] private readonly SharedPopupSystem _popupSystem = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DamagePopupComponent, DamageChangedEvent>(OnDamageChange);
        SubscribeLocalEvent<DamagePopupComponent, InteractHandEvent>(OnInteractHand);
    }

    private void OnDamageChange(Entity<DamagePopupComponent> ent, ref DamageChangedEvent args)
    {
        // Comprehensive validation for all entities involved
        if (!IsEntityValidForPopup(ent) ||
            (args.Origin.HasValue && !IsEntityValidForPopup(args.Origin.Value)))
        {
            return;
        }

        if (args.DamageDelta != null && args.DamageIncreased)
        {
            var damageTotal = args.Damageable.TotalDamage;
            var damageDelta = args.DamageDelta.GetTotal();

            var msg = ent.Comp.Type switch
            {
                DamagePopupType.Delta => damageDelta.ToString(),
                DamagePopupType.Total => damageTotal.ToString(),
                DamagePopupType.Combined => damageDelta + " | " + damageTotal,
                DamagePopupType.Hit => "!",
                _ => "Invalid type",
            };

            // Final validation right before showing popup
            if (IsEntityValidForPopup(ent) &&
                (!args.Origin.HasValue || IsEntityValidForPopup(args.Origin.Value)))
            {
                _popupSystem.PopupPredicted(msg, ent.Owner, args.Origin);
            }
        }
    }

    private void OnInteractHand(Entity<DamagePopupComponent> ent, ref InteractHandEvent args)
    {
        if (!IsEntityValidForPopup(ent) || !IsEntityValidForPopup(args.User))
            return;

        if (ent.Comp.AllowTypeChange)
        {
            var next = (DamagePopupType)(((int)ent.Comp.Type + 1) % Enum.GetValues<DamagePopupType>().Length);
            ent.Comp.Type = next;
            Dirty(ent);

            if (IsEntityValidForPopup(ent) && IsEntityValidForPopup(args.User))
            {
                _popupSystem.PopupPredicted(Loc.GetString("damage-popup-component-switched", ("setting", ent.Comp.Type)), ent.Owner, args.User);
            }
        }
    }

    private bool IsEntityValidForPopup(EntityUid uid)
    {
        return !Deleted(uid) &&
               !Terminating(uid) &&
               EntityManager.EntityExists(uid) &&
               HasComp<MetaDataComponent>(uid) &&
               HasComp<TransformComponent>(uid);
    }
}

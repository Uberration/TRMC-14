using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Prototypes;
using Content.Shared.Damage;

namespace Content.Shared.Projectiles
{
    // In your RMCProjectileDamageComponent definition
[RegisterComponent]
    public sealed partial class RMCProjectileDamageComponent : Component
    {
        [DataField("damage")]
        public DamageSpecifier Damage { get; set; } = new();

        [DataField("radius")]
        public float Radius { get; set; } = 0f;

        [ViewVariables]
        public bool AppliedDamage { get; set; } = false;
    }
}

using Content.Shared.Damage.Components;
using Content.Shared.Item.ItemToggle.Components;
using Robust.Shared.GameStates;

namespace Content.Shared.Stunnable;

/// <summary>
/// Component used for stun batons.
/// Works in combintation with <see cref="StaminaDamageOnHitComponent"/>, <see cref="PredictedBatteryComponent"/> and <see cref="ItemToggleComponent"/>
/// to make the entity require battery charge to deal stamina damage to someone while it is toggled on and used as a weapon.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(StunbatonSystem))]
public sealed partial class StunbatonComponent : Component
{
    /// <summary>
    /// The flat charge required per hit.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float EnergyPerUse = 350;

    /// <summary>
    /// Additional charge required per hit, as a fraction of the battery's max charge.
    /// Total cost per hit is <c>EnergyPerUse + MaxChargeFraction * MaxCharge</c>.
    /// Used to scale high-capacity cells to prevent absurd number of hits.
    /// If 0, only <see cref="EnergyPerUse"/> is consumed.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float MaxChargeFraction = 0f;
}

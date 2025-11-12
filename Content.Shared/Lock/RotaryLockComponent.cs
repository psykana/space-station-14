using Content.Shared.SmartFridge;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Lock;

/// <summary>
/// Allows locking/unlocking, with password
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(true)]
public sealed partial class RotaryLockComponent : Component
{
    /// Determines, is the service panel open
    /// </summary>
    [DataField, AutoNetworkedField]
    public int DialPos = 0;

    /// <summary>
    /// The container ID that this SmartFridge stores its inventory in
    /// </summary>
    [DataField]
    public string Container = "safe_inventory";

    /// <summary>
    /// A list of entries to display in the UI
    /// </summary>
    [DataField, AutoNetworkedField]
    public List<SmartFridgeEntry> Entries = new();

    /// <summary>
    /// A mapping of smart fridge entries to the actual contained contents
    /// </summary>
    [DataField, AutoNetworkedField]
    [Access(typeof(SmartFridgeSystem), Other = AccessPermissions.ReadExecute)]
    public Dictionary<SmartFridgeEntry, HashSet<NetEntity>> ContainedEntries = new();

    /// <summary>
    /// Current lock status
    /// </summary>
    [DataField, AutoNetworkedField]
    public SafeStatus Status = SafeStatus.LOCKED;

    [DataField, AutoNetworkedField]
    public List<int> Tumblers = new();

    [DataField, AutoNetworkedField]
    public int Number_of_tumblers = 3;

    [DataField, AutoNetworkedField]
    public int Current_tumbler_index = 0;

}

[Serializable, NetSerializable]
public enum StorageUiSafeKey : byte
{
    Key,
}

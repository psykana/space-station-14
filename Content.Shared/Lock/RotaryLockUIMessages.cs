using Robust.Shared.Serialization;

namespace Content.Shared.Lock;

[Serializable, NetSerializable]
public sealed class RotaryLockUiState : BoundUserInterfaceState
{
    public int DialPos;
    public SafeStatus Status;
}

[Serializable, NetSerializable]
public sealed class RotaryLockTurnLeftMessage : BoundUserInterfaceMessage
{
    public int Value;
    public RotaryLockTurnLeftMessage(int value) => Value = value;
}
[Serializable, NetSerializable]
public sealed class RotaryLockTurnRightMessage : BoundUserInterfaceMessage
{
    public int Value;
    public RotaryLockTurnRightMessage(int value) => Value = value;
}
[Serializable, NetSerializable]
public sealed class RotaryLockOpenMessage : BoundUserInterfaceMessage
{
}

public enum SafeStatus : byte
{
    LOCKED,
    UNLOCKED, // combination correct but door closed
    OPEN,
    DESTROYED
}

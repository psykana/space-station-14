namespace Content.Shared.UserInterface;

/// <summary>
/// How much a user may do with a UI. Ordered, so comparisons are meaningful.
/// </summary>
public enum UiStatus : sbyte
{
    /// <summary>
    /// Can't have the UI at all: unconscious, or a policy denial (admin-only, spectators blocked, not complex enough).
    /// An open window is closed.
    /// </summary>
    Close = -1,

    /// <summary>
    /// The user could use it in principle, but their conditions block it now, e.g. stunned or cuffed.
    /// Can't open or use; a window that is already open stays open.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// May open and look, but every input is rejected.
    /// </summary>
    ReadOnly = 1,

    /// <summary>
    /// Full use.
    /// </summary>
    Interactive = 2,
}

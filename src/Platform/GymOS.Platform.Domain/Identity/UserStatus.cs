namespace GymOS.Platform.Domain.Identity;

public enum UserStatus
{
    /// <summary>Created but no password set yet — cannot authenticate.</summary>
    PendingActivation = 1,

    Active = 2,

    /// <summary>Blocked by an administrator; all sessions die via security-stamp rotation.</summary>
    Suspended = 3,
}

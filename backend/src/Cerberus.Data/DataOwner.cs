namespace Cerberus.Data;

/// <summary>A row that belongs to one user.</summary>
public interface IUserOwned
{
    string UserId { get; }
}

/// <summary>
/// Whose data a database context may see — the second lock on per-user data
/// (PLAN.md §22 item 8). Every query in the code already filters by user; this
/// is for the one that forgets to.
///
/// One per dependency-injection scope, and unrestricted until told otherwise:
/// - a web request's scope is restricted to no one as soon as the request
///   arrives, and to the signed-in user once the token has been checked. A
///   query through the request's context then returns that user's rows and
///   nothing else, whatever its own conditions say;
/// - a scope a service opens for itself (the job worker, the audit log, the
///   scheduler) stays unrestricted: that code is given the user's id by the
///   caller and works across users by design.
///
/// It is deliberately not ambient (an <c>AsyncLocal</c>): a background loop
/// started from inside a request would inherit that request's user for the
/// rest of its life.
/// </summary>
public sealed class DataOwner
{
    /// <summary>True when the context may only see <see cref="UserId"/>'s rows.</summary>
    public bool Restricted { get; private set; }

    /// <summary>The only user whose rows are visible; null with <see cref="Restricted"/> means nobody's.</summary>
    public string? UserId { get; private set; }

    /// <summary>Nothing owned by a user is visible or writable: the state of a request before sign-in is checked.</summary>
    public void RestrictToNoOne()
    {
        Restricted = true;
        UserId = null;
    }

    /// <summary>Only this user's rows are visible and writable.</summary>
    public void RestrictTo(string userId)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        Restricted = true;
        UserId = userId;
    }
}

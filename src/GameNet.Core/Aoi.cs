namespace GameNet.Core;

/// <summary>
/// Visibility policies for <see cref="EntityCollection.Filter"/>. AOI is not hardcoded into the
/// replication loop: a circle is just the default filter installed when <c>AoiRadius &gt; 0</c>.
/// Replace or compose filters to get any shape/rule you want.
/// </summary>
public static class Aoi
{
    /// <summary>Circle around the session avatar; nothing is visible until an avatar is bound.</summary>
    public static Func<Session, EntityState, bool> Circle(float radius) =>
        Circle(radius, session => session.HasAvatar ? session.Avatar : null);

    /// <summary>Circle around a custom center (return null for "nothing visible").</summary>
    public static Func<Session, EntityState, bool> Circle(float radius, Func<Session, EntityState?> center) =>
        (session, entity) =>
        {
            if (center(session) is not { } origin) return false;
            if (entity.Id == origin.Id) return true; // you always see yourself
            var dx = entity.X - origin.X;
            var dy = entity.Y - origin.Y;
            return dx * dx + dy * dy <= radius * radius;
        };

    /// <summary>All filters must pass (AND).</summary>
    public static Func<Session, EntityState, bool> All(params Func<Session, EntityState, bool>[] filters) =>
        (session, entity) =>
        {
            foreach (var filter in filters)
                if (!filter(session, entity)) return false;
            return true;
        };

    /// <summary>Any filter passing is enough (OR).</summary>
    public static Func<Session, EntityState, bool> Any(params Func<Session, EntityState, bool>[] filters) =>
        (session, entity) =>
        {
            foreach (var filter in filters)
                if (filter(session, entity)) return true;
            return false;
        };
}

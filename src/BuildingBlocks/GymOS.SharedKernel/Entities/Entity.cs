namespace GymOS.SharedKernel.Entities;

/// <summary>
/// Base type for all persisted domain objects. Identity equality: two entities are
/// equal when they are the same concrete type with the same non-empty <see cref="Id"/>.
/// Transient entities (empty id) are equal only by reference.
/// </summary>
public abstract class Entity : IEquatable<Entity>
{
    protected Entity(Guid id) => Id = id;

    /// <summary>Parameterless constructor reserved for ORM materialization.</summary>
    protected Entity()
    {
    }

    public Guid Id { get; protected init; }

    public bool Equals(Entity? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other.GetType() != GetType())
        {
            return false;
        }

        if (Id == Guid.Empty || other.Id == Guid.Empty)
        {
            return false;
        }

        return Id == other.Id;
    }

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);

    public static bool operator ==(Entity? left, Entity? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(Entity? left, Entity? right) => !(left == right);
}

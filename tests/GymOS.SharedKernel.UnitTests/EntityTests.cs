using GymOS.SharedKernel.Entities;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

public sealed class EntityTests
{
    private sealed class TestEntity : Entity
    {
        public TestEntity(Guid id)
            : base(id)
        {
        }

        public TestEntity()
        {
        }
    }

    private sealed class OtherEntity : Entity
    {
        public OtherEntity(Guid id)
            : base(id)
        {
        }
    }

    [Fact]
    public void Entities_WithSameTypeAndId_AreEqual()
    {
        var id = Guid.NewGuid();
        var first = new TestEntity(id);
        var second = new TestEntity(id);

        Assert.Equal(first, second);
        Assert.True(first == second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void Entities_WithDifferentIds_AreNotEqual()
    {
        var first = new TestEntity(Guid.NewGuid());
        var second = new TestEntity(Guid.NewGuid());

        Assert.NotEqual(first, second);
        Assert.True(first != second);
    }

    [Fact]
    public void Entities_OfDifferentTypes_WithSameId_AreNotEqual()
    {
        var id = Guid.NewGuid();

        Assert.False(new TestEntity(id).Equals(new OtherEntity(id)));
    }

    [Fact]
    public void TransientEntities_AreEqualOnlyByReference()
    {
        var first = new TestEntity();
        var second = new TestEntity();

        Assert.NotEqual(first, second);
        Assert.Equal(first, first);
    }

    [Fact]
    public void Entity_ComparedToNull_IsNotEqual()
    {
        var entity = new TestEntity(Guid.NewGuid());

        Assert.False(entity.Equals(null));
        Assert.True(entity != null);
    }
}

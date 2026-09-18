using GymOS.BuildingBlocks.Infrastructure.Jobs;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

public sealed class JobRunErrorTests
{
    [Fact]
    public void ItIsTheTypeNameAndMessageOnly()
    {
        var exception = new InvalidOperationException("renewal failed for gym 7");

        Assert.Equal("InvalidOperationException: renewal failed for gym 7", JobRunError.Describe(exception));
    }

    [Fact]
    public void ItNeverCarriesAStackTraceOrInnerException()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("outer", new ArgumentException("inner detail with Server=secret"));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        string described = JobRunError.Describe(thrown);

        Assert.DoesNotContain("   at ", described, StringComparison.Ordinal);
        Assert.DoesNotContain("inner detail", described, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsTruncatedToTheColumnWidth()
    {
        var exception = new InvalidOperationException(new string('x', 5000));

        Assert.Equal(JobRunError.MaxLength, JobRunError.Describe(exception).Length);
    }
}

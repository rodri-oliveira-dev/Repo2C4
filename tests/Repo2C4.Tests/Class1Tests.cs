using AwesomeAssertions;
using NSubstitute;
using Repo2C4;
using Xunit;

namespace Repo2C4.Tests;

public sealed class Class1Tests
{
    [Fact]
    public void CanCreateLibraryType()
    {
        var instance = new Class1();

        instance.Should().NotBeNull();
    }

    [Fact]
    public void SupportsSubstitutesInTests()
    {
        var dependency = Substitute.For<IValueProvider>();
        dependency.GetValue().Returns("expected");

        dependency.GetValue().Should().Be("expected");
    }

    public interface IValueProvider
    {
        string GetValue();
    }
}

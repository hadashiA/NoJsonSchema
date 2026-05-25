using NoJsonSchema.Core.Naming;
using Xunit;

namespace NoJsonSchema.Core.Tests.Naming;

public class NameFactoryTests
{
    [Theory]
    [InlineData("foo", "Foo")]
    [InlineData("client_id", "ClientId")]
    [InlineData("client-id", "ClientId")]
    [InlineData("clientID", "ClientID")]
    [InlineData("linesStartAt1", "LinesStartAt1")]
    [InlineData("InitializeRequest", "InitializeRequest")]
    [InlineData("$schema", "Schema")]
    [InlineData("a/b", "AB")]
    [InlineData("1foo", "_1foo")]
    [InlineData("", "Unnamed")]
    [InlineData("___", "Unnamed")]
    public void ToTypeIdentifier(string raw, string expected)
    {
        Assert.Equal(expected, NameFactory.ToTypeIdentifier(raw));
    }

    [Fact]
    public void MakeUniqueTypeName_SecondCollisionGetsSuffix()
    {
        var f = new NameFactory();
        Assert.Equal("Foo", f.MakeUniqueTypeName("foo"));
        Assert.Equal("Foo2", f.MakeUniqueTypeName("foo"));
        Assert.Equal("Foo3", f.MakeUniqueTypeName("foo"));
    }

    [Fact]
    public void MakeUniqueTypeName_RespectsReservation()
    {
        var f = new NameFactory();
        f.ReserveTypeName("DapSerializer");
        Assert.Equal("DapSerializer2", f.MakeUniqueTypeName("DapSerializer"));
    }

    [Fact]
    public void MakeUniquePropertyName_TracksSiblings()
    {
        var siblings = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("Foo", NameFactory.MakeUniquePropertyName("foo", siblings));
        Assert.Equal("Foo2", NameFactory.MakeUniquePropertyName("foo", siblings));
    }

    [Fact]
    public void EscapeIfReserved_PrefixesAtSign()
    {
        Assert.Equal("@class", NameFactory.EscapeIfReserved("class"));
        Assert.Equal("Class", NameFactory.EscapeIfReserved("Class"));
    }
}

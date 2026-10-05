using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class UndergroundObservationTests
{
    [Fact]
    public void AnUnpairedInputOmitsItsPartnerWhileRetainingNativeCounts()
    {
        var native = JsonSerializer.Deserialize<ObservedUndergroundBelt>(
            """{"type":"input","neighbourCount":0,"transportLineCount":4}""", Protocol.Json)!;
        Assert.Null(native.NeighbourId);
        Assert.Equal(0, native.NeighbourCount);
        Assert.Equal(4, native.TransportLineCount);
    }

    [Theory]
    [InlineData("""{"neighbourCount":0,"transportLineCount":4}""")]
    [InlineData("""{"type":"input","transportLineCount":4}""")]
    [InlineData("""{"type":"input","neighbourCount":0}""")]
    public void MissingNativeTypeOrCountsRemainInvalid(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ObservedUndergroundBelt>(json, Protocol.Json));
}

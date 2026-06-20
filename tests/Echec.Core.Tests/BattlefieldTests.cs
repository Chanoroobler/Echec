using Echec.Core.Map;
using Xunit;

namespace Echec.Core.Tests;

public class BattlefieldTests
{
    [Fact]
    public void CreateFlat_HasGivenDimensions()
    {
        var field = Battlefield.CreateFlat(5, 5);

        Assert.Equal(5, field.Width);
        Assert.Equal(5, field.Height);
    }

    [Fact]
    public void CreateFlat_FillsEveryCellWithTerrain()
    {
        var field = Battlefield.CreateFlat(5, 5, TerrainType.Grass);

        Assert.All(field.Cells(), cell => Assert.Equal(TerrainType.Grass, field[cell].Terrain));
        Assert.Equal(25, field.Cells().Count());
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 4, true)]
    [InlineData(5, 0, false)]
    [InlineData(-1, 2, false)]
    public void Contains_RespectsBounds(int column, int row, bool expected)
    {
        var field = Battlefield.CreateFlat(5, 5);

        Assert.Equal(expected, field.Contains(new Cell(column, row)));
    }
}

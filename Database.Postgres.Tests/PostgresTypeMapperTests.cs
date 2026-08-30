using Database.Postgres;
using FluentAssertions;
using Xunit;

namespace Database.Postgres.Tests;

public class PostgresTypeMapperTests
{
    [Theory]
    [InlineData(ProviderAgnosticType.Integer, "INTEGER")]
    [InlineData(ProviderAgnosticType.BigInt, "BIGINT")]
    [InlineData(ProviderAgnosticType.Text, "TEXT")]
    [InlineData(ProviderAgnosticType.Boolean, "BOOLEAN")]
    [InlineData(ProviderAgnosticType.Timestamp, "TIMESTAMP")]
    public void ToSqlType_TypesZonderParameters_MapptCorrect(ProviderAgnosticType type, string expected)
    {
        var column = new ColumnDefinition("kolom", type);

        PostgresTypeMapper.ToSqlType(column).Should().Be(expected);
    }

    [Fact]
    public void ToSqlType_VarCharMetLength_GeeftLengteInDdl()
    {
        var column = new ColumnDefinition("naam", ProviderAgnosticType.VarChar, Length: 100);

        PostgresTypeMapper.ToSqlType(column).Should().Be("VARCHAR(100)");
    }

    [Fact]
    public void ToSqlType_VarCharZonderLength_GooitInvalidOperationException()
    {
        var column = new ColumnDefinition("naam", ProviderAgnosticType.VarChar);

        var act = () => PostgresTypeMapper.ToSqlType(column);

        act.Should().Throw<InvalidOperationException>().WithMessage("*naam*Length*");
    }

    [Fact]
    public void ToSqlType_DecimalMetPrecisionEnScale_GeeftBeideInDdl()
    {
        var column = new ColumnDefinition("bedrag", ProviderAgnosticType.Decimal, Precision: 18, Scale: 4);

        PostgresTypeMapper.ToSqlType(column).Should().Be("NUMERIC(18,4)");
    }

    [Fact]
    public void ToSqlType_DecimalZonderScale_ValtTerugOpNul()
    {
        var column = new ColumnDefinition("bedrag", ProviderAgnosticType.Decimal, Precision: 18);

        PostgresTypeMapper.ToSqlType(column).Should().Be("NUMERIC(18,0)");
    }

    [Fact]
    public void ToSqlType_DecimalZonderPrecision_GooitInvalidOperationException()
    {
        var column = new ColumnDefinition("bedrag", ProviderAgnosticType.Decimal);

        var act = () => PostgresTypeMapper.ToSqlType(column);

        act.Should().Throw<InvalidOperationException>().WithMessage("*bedrag*Precision*");
    }
}

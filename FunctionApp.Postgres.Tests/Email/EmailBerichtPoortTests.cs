using AwesomeAssertions;
using FunctionApp.Postgres.Email;
using Xunit;

namespace FunctionApp.Postgres.Tests.Email;

public sealed class EmailBerichtPoortTests
{
    [Fact]
    public void Body_BijNullWordtLegeStringDoorgestuurdAanDeGedeeldeFlow()
    {
        EmailBerichtPoort.Body(new InkomendBericht { Body = null }).Should().BeEmpty();
    }

    [Fact]
    public void Body_BijWaardeBlijftDeInhoudOngewijzigd()
    {
        EmailBerichtPoort.Body(new InkomendBericht { Body = "berichtinhoud" }).Should().Be("berichtinhoud");
    }
}

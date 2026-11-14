using AwesomeAssertions;
using SportlinkFunction.Email;
using Xunit;

namespace FunctionApp.Tests.Email;

public sealed class EmailBerichtPoortTests
{
    [Fact]
    public void Body_BewaartNietNullBodyOngewijzigd()
    {
        EmailBerichtPoort.Body(new InkomendBericht { Body = "berichtinhoud" }).Should().Be("berichtinhoud");
    }
}

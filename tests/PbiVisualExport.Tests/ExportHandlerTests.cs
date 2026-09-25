using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace PbiVisualExport.Tests;

public class ExportHandlerTests
{
    private static readonly DateTime At = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Theory]
    [InlineData("Sales: \"Q1\"/Q2", "Sales_ _Q1__Q2-20240102-030405.csv")]
    [InlineData("a<b>c|d*e?f\\g,h", "a_b_c_d_e_f_g_h-20240102-030405.csv")]
    [InlineData("  padded  ", "padded-20240102-030405.csv")]
    [InlineData("tab\there", "tab_here-20240102-030405.csv")]
    [InlineData(null, "export-20240102-030405.csv")]
    [InlineData("   ", "export-20240102-030405.csv")]
    public void BuildFileName_SanitizesTheTitle(string? title, string expected) =>
        Assert.Equal(expected, ExportHandler.BuildFileName(title, At));

    [Fact]
    public void BuildFileName_TruncatesLongTitles()
    {
        var name = ExportHandler.BuildFileName(new string('x', 200), At);
        Assert.Equal(new string('x', 80) + "-20240102-030405.csv", name);
    }

    [Fact]
    public void UserOf_Anonymous_IsKeyedByAddress()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        Assert.Equal(("anon:10.1.2.3", (string?)null), ExportHandler.UserOf(http));
    }

    [Theory]
    [InlineData("http://schemas.microsoft.com/identity/claims/objectidentifier")]
    [InlineData("oid")]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("sub")]
    public void UserOf_UsesTheFirstIdClaim(string claimType)
    {
        var http = Authenticated(new Claim(claimType, "id-1"), new Claim(ClaimTypes.Name, "Ann"));
        Assert.Equal(("user:id-1", "Ann"), ExportHandler.UserOf(http));
    }

    [Fact]
    public void UserOf_PrefersObjectIdOverOtherClaims()
    {
        var http = Authenticated(new Claim("sub", "subject"), new Claim("oid", "object"));
        Assert.Equal(("user:object", "object"), ExportHandler.UserOf(http));
    }

    [Fact]
    public void UserOf_FallsBackToNameThenAConstant()
    {
        Assert.Equal(("user:Ann", "Ann"), ExportHandler.UserOf(Authenticated(new Claim(ClaimTypes.Name, "Ann"))));
        Assert.Equal(("user:authenticated", "authenticated"), ExportHandler.UserOf(Authenticated()));
    }

    private static HttpContext Authenticated(params Claim[] claims) =>
        new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
}

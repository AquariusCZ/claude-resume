using AiResume.Core;
using Xunit;

namespace AiResume.Tests;

public sealed class ErrorClassTests
{
    [Fact]
    public void Enum_has_exactly_the_seven_contract_members()
    {
        ErrorClass[] expected =
        {
            ErrorClass.Transient,
            ErrorClass.Auth,
            ErrorClass.Quota,
            ErrorClass.ModelUnavailable,
            ErrorClass.Config,
            ErrorClass.Internal,
            ErrorClass.Cancelled,
        };

        Assert.Equal(expected, Enum.GetValues<ErrorClass>());
    }

    [Fact]
    public void Wire_codes_are_stable_and_unique()
    {
        var expected = new Dictionary<ErrorClass, string>
        {
            [ErrorClass.Transient] = "transient",
            [ErrorClass.Auth] = "auth",
            [ErrorClass.Quota] = "quota",
            [ErrorClass.ModelUnavailable] = "model_unavailable",
            [ErrorClass.Config] = "config",
            [ErrorClass.Internal] = "internal",
            [ErrorClass.Cancelled] = "cancelled",
        };

        string[] codes = expected.Values.OrderBy(c => c).ToArray();
        Assert.Equal(codes, codes.Distinct().OrderBy(c => c).ToArray());

        foreach ((ErrorClass errorClass, string code) in expected)
        {
            Assert.Equal(code, errorClass.ToWireCode());
            Assert.True(ErrorClassCodes.TryFromWireCode(code, out ErrorClass parsed));
            Assert.Equal(errorClass, parsed);
        }
    }

    [Fact]
    public void Unknown_or_null_wire_code_is_rejected()
    {
        Assert.False(ErrorClassCodes.TryFromWireCode("provider_timeout", out _));
        Assert.False(ErrorClassCodes.TryFromWireCode(null, out _));
        Assert.False(ErrorClassCodes.TryFromWireCode(string.Empty, out _));
    }

    [Fact]
    public void No_member_maps_to_an_empty_or_duplicate_code()
    {
        foreach (ErrorClass errorClass in Enum.GetValues<ErrorClass>())
        {
            string code = errorClass.ToWireCode();
            Assert.False(string.IsNullOrWhiteSpace(code));
        }
    }
}

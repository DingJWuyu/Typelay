using System.Text.Json;
using TypeLay.Windows.Testing;
using Xunit;

namespace TypeLay.Windows.Contracts.Tests;

public sealed class BrowserExtensionMetadataResponseTests
{
    [Theory]
    [InlineData("{\"exceptionDetails\":{\"exception\":{\"className\":\"ReferenceError\"}},\"result\":{\"type\":\"object\",\"subtype\":\"error\"}}", "exception", true, "reference-error", "Object", "object", "error", false)]
    [InlineData("{\"result\":{\"type\":\"undefined\"}}", "value-missing", false, "unavailable", "Object", "undefined", "unavailable", false)]
    [InlineData("{\"result\":{\"type\":\"object\",\"value\":{\"id\":\"synthetic\",\"manifest\":{}}}}", "value-present", false, "unavailable", "Object", "object", "unavailable", true)]
    [InlineData("{\"exceptionDetails\":null,\"result\":{\"value\":null}}", "exception", true, "unavailable", "Object", "unavailable", "unavailable", true)]
    [InlineData("{\"result\":null}", "result-invalid", false, "unavailable", "Null", "unavailable", "unavailable", false)]
    [InlineData("{}", "result-invalid", false, "unavailable", "Undefined", "unavailable", "unavailable", false)]
    [InlineData("null", "result-invalid", false, "unavailable", "Undefined", "unavailable", "unavailable", false)]
    public void ExistingEvaluationResponseDistinguishesExceptionMissingValueAndMalformedResult(string json,
        string outcome, bool exception, string exceptionClass, string kind, string type, string subtype, bool value)
    {
        using var document = JsonDocument.Parse(json);
        var response = document.RootElement;
        string before = response.GetRawText();
        Assert.Equal(new(outcome, exception, exceptionClass, kind, type, subtype, value),
            BrowserExtensionMetadataResponse.Observe(response));
        Assert.Equal(before, response.GetRawText());
    }

    [Theory]
    [InlineData("TypeError", "type-error")]
    [InlineData("SyntaxError", "syntax-error")]
    [InlineData("RangeError", "range-error")]
    [InlineData("Error", "error")]
    [InlineData("CustomError secret-url", "other")]
    [InlineData("TypeError: private-message", "other")]
    public void ExceptionClassIsAnExactAllowlistNotExceptionText(string exceptionClass, string category)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            exceptionDetails = new
            {
                text = "private-message",
                url = "https://private.invalid/path",
                exception = new
                {
                    className = exceptionClass,
                    description = "private-stack"
                }
            },
            result = new
            {
                type = "custom-private-type",
                subtype = "custom-private-subtype",
                objectId = "private-id"
            }
        });
        var observed = BrowserExtensionMetadataResponse.Observe(response);
        Assert.Equal(category, observed.ExceptionClass);
        Assert.Equal("other", observed.ResultType);
        Assert.Equal("other", observed.ResultSubtype);
        string diagnostic = JsonSerializer.Serialize(observed);
        Assert.DoesNotContain("private", diagnostic);
        Assert.DoesNotContain("secret", diagnostic);
        Assert.DoesNotContain("https", diagnostic);
    }

    [Fact]
    public void ByValueMetadataIsNotPublishedOrTreatedAsAValidatedIdentity()
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            result = new
            {
                type = "object",
                value = new
                {
                    id = "private-id",
                    manifest = new
                    {
                        name = "private-extension"
                    }
                }
            }
        });
        var observed = BrowserExtensionMetadataResponse.Observe(response);
        Assert.Equal("value-present", observed.Outcome);
        Assert.True(observed.ValuePresent);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(observed));
    }

    [Fact]
    public void MalformedExceptionAndTypeFieldsRemainNonSensitiveObservations()
    {
        using var document = JsonDocument.Parse("{\"exceptionDetails\":42,\"result\":{\"type\":{},\"subtype\":[],\"value\":false}}");
        var observed = BrowserExtensionMetadataResponse.Observe(document.RootElement);
        Assert.True(observed.ExceptionPresent);
        Assert.Equal("exception", observed.Outcome);
        Assert.Equal("unavailable", observed.ExceptionClass);
        Assert.Equal("unavailable", observed.ResultType);
        Assert.Equal("unavailable", observed.ResultSubtype);
        Assert.True(observed.ValuePresent);
    }
}

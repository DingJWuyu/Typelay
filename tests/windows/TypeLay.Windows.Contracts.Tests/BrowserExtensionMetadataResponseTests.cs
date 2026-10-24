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
                lineNumber = 3,
                columnNumber = 7,
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
        Assert.Equal(3, observed.ExceptionLineNumber);
        Assert.Equal(7, observed.ExceptionColumnNumber);
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
                    availability = new
                    {
                        chromePresent = true,
                        runtimePresent = true,
                        getManifestCallable = true
                    },
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
        Assert.True(observed.ChromePresent);
        Assert.True(observed.RuntimePresent);
        Assert.True(observed.GetManifestCallable);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void EvaluationAvailabilityPreservesOnlyActualBooleanObservations(bool chrome, bool runtime, bool callable)
    {
        var response = JsonSerializer.SerializeToElement(new
        {
            result = new
            {
                type = "object",
                value = new
                {
                    availability = new
                    {
                        chromePresent = chrome,
                        runtimePresent = runtime,
                        getManifestCallable = callable
                    }
                }
            }
        });
        var observed = BrowserExtensionMetadataResponse.Observe(response);
        Assert.Equal(chrome, observed.ChromePresent);
        Assert.Equal(runtime, observed.RuntimePresent);
        Assert.Equal(callable, observed.GetManifestCallable);
        Assert.True(observed.ValuePresent);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("{\"availability\":null}")]
    [InlineData("{\"availability\":{\"chromePresent\":\"true\",\"runtimePresent\":0,\"getManifestCallable\":{}}}")]
    public void MissingOrMalformedAvailabilityDoesNotInventBooleanObservations(string value)
    {
        using var document = JsonDocument.Parse("{\"result\":{\"value\":" + value + "}}");
        var observed = BrowserExtensionMetadataResponse.Observe(document.RootElement);
        Assert.Null(observed.ChromePresent);
        Assert.Null(observed.RuntimePresent);
        Assert.Null(observed.GetManifestCallable);
    }

    [Theory]
    [InlineData("ReferenceError", "reference-error")]
    [InlineData("TypeError", "type-error")]
    public void MetadataExceptionDoesNotClaimAvailabilityFromAnExceptionalResponse(string exceptionClass, string category)
    {
        using var document = JsonDocument.Parse("""
            {"exceptionDetails":{"exception":{"className":"EXCEPTION_CLASS"},"lineNumber":3,"columnNumber":7},
             "result":{"type":"object","subtype":"error","value":{"availability":{
                 "chromePresent":true,"runtimePresent":true,"getManifestCallable":true}}}}
            """.Replace("EXCEPTION_CLASS", exceptionClass, StringComparison.Ordinal));
        var observed = BrowserExtensionMetadataResponse.Observe(document.RootElement);
        Assert.Equal("exception", observed.Outcome);
        Assert.Equal(category, observed.ExceptionClass);
        Assert.Null(observed.ChromePresent);
        Assert.Null(observed.RuntimePresent);
        Assert.Null(observed.GetManifestCallable);
        Assert.Equal(3, observed.ExceptionLineNumber);
        Assert.Equal(7, observed.ExceptionColumnNumber);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2147483647", 2147483647)]
    [InlineData("-1", null)]
    [InlineData("1.5", null)]
    [InlineData("1.0", null)]
    [InlineData("2147483648", null)]
    [InlineData("\"7\"", null)]
    [InlineData("null", null)]
    [InlineData("true", null)]
    public void ExceptionPositionsRequireStrictNonNegativeIntegers(string position, int? expected)
    {
        using var document = JsonDocument.Parse("{\"exceptionDetails\":{\"lineNumber\":" + position
            + ",\"columnNumber\":" + position + "},\"result\":{\"type\":\"object\",\"subtype\":\"error\"}}");
        var observed = BrowserExtensionMetadataResponse.Observe(document.RootElement);
        Assert.Equal(expected, observed.ExceptionLineNumber);
        Assert.Equal(expected, observed.ExceptionColumnNumber);
    }
}

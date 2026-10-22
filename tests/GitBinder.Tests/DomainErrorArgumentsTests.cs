using GitBinder.Domain.Common;

namespace GitBinder.Tests;

public sealed class DomainErrorArgumentsTests
{
    [Fact]
    public void Constructor_SingleArgument_IsDisplayArgumentNotTechnicalDetails()
    {
        var error = new DomainError("PROJECT_REPOSITORY_NOT_FOUND", "/projects/local-only");

        Assert.Equal("/projects/local-only", Assert.Single(error.Arguments));
        Assert.Null(error.TechnicalDetails);
    }

    [Fact]
    public void Constructor_MultipleArguments_PreservesEveryDisplayArgument()
    {
        var error = new DomainError("TRANSFER_AUTH_FAILED", "fetch", "128");

        Assert.Equal(new[] { "fetch", "128" }, error.Arguments);
        Assert.Null(error.TechnicalDetails);
    }

    [Fact]
    public void Constructor_NoArguments_HasEmptyDisplayArguments()
    {
        var error = new DomainError("PROJECT_PATH_REQUIRED");

        Assert.Empty(error.Arguments);
        Assert.Null(error.TechnicalDetails);
    }

    [Fact]
    public void Constructor_ExplicitTechnicalDetails_StaysSeparateFromDisplayArguments()
    {
        var error = new DomainError("PROJECT_REPOSITORY_NOT_FOUND",
            technicalDetails: "private diagnostic text", arguments: ["/projects/local-only"]);

        Assert.Equal("private diagnostic text", error.TechnicalDetails);
        Assert.Equal("/projects/local-only", Assert.Single(error.Arguments));
    }

    [Fact]
    public void Constructor_TechnicalDetailsWithoutDisplayArguments_DoesNotPromoteDiagnostics()
    {
        var error = new DomainError("ACCOUNT_KEY_IMPORT_FAILED",
            technicalDetails: "private diagnostic text", arguments: []);

        Assert.Equal("private diagnostic text", error.TechnicalDetails);
        Assert.Empty(error.Arguments);
    }
}

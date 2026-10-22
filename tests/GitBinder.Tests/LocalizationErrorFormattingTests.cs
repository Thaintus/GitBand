using System.Globalization;
using GitBinder.Application.Common;
using GitBinder.Desktop.Localization;
using GitBinder.Domain.Common;

namespace GitBinder.Tests;

public sealed class LocalizationErrorFormattingTests
{
    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public async Task GetString_DomainErrorSingleArgument_ShowsPathInsteadOfArrayType(string culture)
    {
        ILocalizationService localization = new LocalizationService();
        await localization.SetCultureAsync(new CultureInfo(culture));
        const string path = @"D:\projects\local-only";
        var error = new DomainError("PROJECT_REPOSITORY_NOT_FOUND", path);

        var message = localization.GetString(error.Code, error.Arguments);

        Assert.Equal(string.Format(CultureInfo.InvariantCulture, localization.GetString(error.Code), path), message);
        Assert.Contains(path, message);
        Assert.DoesNotContain("System.String[]", message);
    }

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("en-US")]
    public async Task GetString_DomainErrorMultipleArguments_ExpandsEveryPlaceholder(string culture)
    {
        ILocalizationService localization = new LocalizationService();
        await localization.SetCultureAsync(new CultureInfo(culture));
        var error = new DomainError("TRANSFER_AUTH_FAILED", arguments: ["fetch", "128"]);

        var message = localization.GetString(error.Code, error.Arguments);

        Assert.Equal(string.Format(CultureInfo.InvariantCulture, localization.GetString(error.Code), "fetch", "128"), message);
        Assert.Contains("fetch", message);
        Assert.Contains("128", message);
        Assert.DoesNotContain("System.String[]", message);
    }

    [Fact]
    public void Format_ReadOnlyListArguments_UsesTheSameExpansion()
    {
        ILocalizationService localization = new LocalizationService();
        IReadOnlyList<string> arguments = new List<string> { "fetch", "128" }.AsReadOnly();

        var message = localization.Format("TRANSFER_AUTH_FAILED", arguments);

        Assert.Equal(localization.GetString("TRANSFER_AUTH_FAILED", "fetch", "128"), message);
    }

    [Fact]
    public void GetString_EmptyDomainArguments_ReturnsResourceWithoutArrayType()
    {
        ILocalizationService localization = new LocalizationService();
        var error = new DomainError("PROJECT_PATH_REQUIRED");

        var message = localization.GetString(error.Code, error.Arguments);

        Assert.Equal(localization.GetString(error.Code), message);
    }

    [Fact]
    public void GetString_OrdinaryObjectArguments_PreservesNumericFormatting()
    {
        ILocalizationService localization = new LocalizationService();

        var message = localization.GetString("Projects.VisibleCount", 2, 10);

        Assert.Equal(string.Format(CultureInfo.InvariantCulture,
            localization.GetString("Projects.VisibleCount"), 2, 10), message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void GetString_MissingArguments_ReturnsSafeTemplateWithoutThrowing(int argumentCount)
    {
        ILocalizationService localization = new LocalizationService();
        var error = new DomainError("TRANSFER_AUTH_FAILED", Enumerable.Repeat("fetch", argumentCount).ToArray());

        var message = localization.GetString(error.Code, error.Arguments);

        Assert.Equal(localization.GetString(error.Code), message);
    }

    [Fact]
    public void GetString_TechnicalDetails_AreNeverUsedAsDisplayArguments()
    {
        ILocalizationService localization = new LocalizationService();
        var error = new DomainError("PROJECT_REPOSITORY_NOT_FOUND",
            technicalDetails: "private diagnostic text", arguments: ["/projects/local-only"]);

        var message = localization.GetString(error.Code, error.Arguments);

        Assert.Contains("/projects/local-only", message);
        Assert.DoesNotContain(error.TechnicalDetails!, message);
    }
}

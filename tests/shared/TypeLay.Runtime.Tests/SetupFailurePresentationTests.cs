using TypeLay.Runtime;
using Xunit;

namespace TypeLay.Runtime.Tests;

public sealed class SetupFailurePresentationTests
{
    [Theory]
    [InlineData("network", SetupFailureCategory.Network, "网络", "网络或代理")]
    [InlineData("disk-space", SetupFailureCategory.DiskSpace, "空间", "释放")]
    [InlineData("integrity", SetupFailureCategory.Integrity, "校验", "重新获取")]
    [InlineData("permission", SetupFailureCategory.Permission, "访问", "权限")]
    [InlineData("runtime", SetupFailureCategory.Runtime, "运行环境", "准备")]
    [InlineData("unknown", SetupFailureCategory.Unknown, "原因尚未确定", "诊断")]
    public void CategoryAndWireCodeProvideTheSameActionableFailure(string code, SetupFailureCategory category,
        string reason, string nextStep)
    {
        var windows = SetupFailurePresentation.Create(category);
        var linux = SetupFailurePresentation.FromCode(code);
        Assert.Equal(windows, linux);
        Assert.Equal(code, windows.Code);
        Assert.Contains(reason, windows.Message);
        Assert.Contains(nextStep, windows.NextStep);
        Assert.Equal("重新准备", windows.RetryLabel);
        Assert.DoesNotContain("原可用版本保持不变", windows.Message + windows.NextStep);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("untrusted/path/with-input-text")]
    public void UnknownFailuresNeverEchoUntrustedDetails(string? code)
    {
        Assert.Equal(SetupFailurePresentation.Create(SetupFailureCategory.Unknown),
            SetupFailurePresentation.FromCode(code));
    }

    [Fact]
    public void UserPauseHasSeparateNonFailureGuidance()
    {
        Assert.Contains("已暂停", SetupFailurePresentation.PausedMessage);
        Assert.DoesNotContain("失败", SetupFailurePresentation.PausedMessage);
        Assert.Equal("继续准备", SetupFailurePresentation.ResumeLabel);
    }
}

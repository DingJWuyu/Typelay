using System.Runtime.CompilerServices;
using TypeLay.Core;
using Xunit;
namespace TypeLay.Core.Tests;

public sealed class FieldPolicyTests
{
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null) => Assert.True(value, expression);
    private static readonly FieldMetadata normal = new FieldMetadata("msedge", "Message", "body", "", "multiline=true", false, true, false, false, true, true, true, true);
    [Fact(DisplayName = "policy-allows-labelled-message")]
    public void PolicyAllowsLabelledMessage()
    {
        Check(FieldPolicy.Allows(normal, []));
    }

    [Fact(DisplayName = "policy-allows-chinese-comment")]
    public void PolicyAllowsChineseComment()
    {
        Check(FieldPolicy.Allows(normal with
        {
            Label = "评论",
            AutomationId = ""
        }, []));
    }

    [Fact(DisplayName = "policy-unlabelled-edit-is-discovered")]
    public void PolicyUnlabelledEditIsDiscovered()
    {
        Check(FieldPolicy.Allows(normal with
        {
            Label = "",
            AutomationId = ""
        }, []));
    }

    [Fact(DisplayName = "policy-desktop-edit-is-discovered")]
    public void PolicyDesktopEditIsDiscovered()
    {
        Check(FieldPolicy.Allows(normal with
        {
            Process = "notepad",
            InDocument = false
        }, []));
    }

    [Fact(DisplayName = "policy-windows-search-is-discovered")]
    public void PolicyWindowsSearchIsDiscovered()
    {
        Check(FieldPolicy.Allows(normal with
        {
            Process = "searchhost",
            Label = "Search",
            InDocument = false
        }, []));
    }

    [Fact(DisplayName = "policy-own-app-is-excluded")]
    public void PolicyOwnAppIsExcluded()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            Process = "typelay.app"
        }, []));
    }

    [Fact(DisplayName = "policy-browser-address-bar-is-excluded")]
    public void PolicyBrowserAddressBarIsExcluded()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            Label = "address bar",
            InDocument = false
        }, []));
    }

    [Fact(DisplayName = "policy-exclusion-case-insensitive")]
    public void PolicyExclusionCaseInsensitive()
    {
        Check(!FieldPolicy.Allows(normal, ["MSEDGE"]));
    }

    [Fact(DisplayName = "policy-password-first")]
    public void PolicyPasswordFirst()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            Password = true
        }, []));
    }

    [Fact(DisplayName = "policy-textedit-is-not-a-discovery-requirement")]
    public void PolicyTexteditIsNotADiscoveryRequirement()
    {
        Check(FieldPolicy.Allows(normal with
        {
            HasTextEdit = false
        }, []));
    }

    [Fact(DisplayName = "policy-readonly")]
    public void PolicyReadonly()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            ReadOnly = true
        }, []));
    }

    [Fact(DisplayName = "policy-offscreen")]
    public void PolicyOffscreen()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            Offscreen = true
        }, []));
    }

    [Fact(DisplayName = "policy-disabled")]
    public void PolicyDisabled()
    {
        Check(!FieldPolicy.Allows(normal with
        {
            Enabled = false
        }, []));
    }

    [Fact(DisplayName = "policy-text-only-editor-is-discovered")]
    public void PolicyTextOnlyEditorIsDiscovered()
    {
        Check(FieldPolicy.Allows(normal with
        {
            HasValue = false
        }, []));
    }

    [Theory]
    [InlineData("Password message")]
    [InlineData("PIN message")]
    [InlineData("CVV comment")]
    [InlineData("message verification")]
    [InlineData("API key reply")]
    [InlineData("银行卡消息")]
    [InlineData("信用卡正文")]
    [InlineData("消息验证码")]
    [InlineData("private token message")]
    [InlineData("message terminal")]
    [InlineData("shell comment")]
    [InlineData("monaco body")]
    public void SensitiveOrMachineFieldsAreExcluded(string label)
    {
        var field = new FieldMetadata("msedge", "Message", "body", "", "multiline=true", false, true, false, false, true, true, true, true);
        Assert.False(FieldPolicy.Allows(field with
        {
            Label = label
        }, []));
    }
}

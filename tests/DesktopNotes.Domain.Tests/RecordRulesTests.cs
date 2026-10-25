using DesktopNotes.Domain;
using Xunit;

namespace DesktopNotes.Domain.Tests;

public class RecordRulesTests
{
    [Fact]
    public void Generated_name_does_not_split_an_emoji()
    {
        var prefix = new string('字', 29) + "😀";
        Assert.Equal(prefix + "…", DisplayName.Create(" ", prefix + "尾"));
    }

    [Theory]
    [InlineData("合同", "合同审批", true)]
    [InlineData("合同", "合 同", false)]
    [InlineData("%_", "预算50%_说明", true)]
    [InlineData("contract", "Contract", false)]
    [InlineData("  合同 ", "签署合同文件", true)]
    [InlineData("", "任意内容", true)]
    public void Query_is_literal(string query, string body, bool expected)
        => Assert.Equal(expected, LiteralSearch.Matches(query, body));

    [Fact]
    public void Explicit_empty_due_does_not_inherit()
    {
        var none = new EffectiveFields(null, null, Priority.Normal,
            null, DueSpec.Empty, null);
        var parent = none with
        {
            Due = new(DueKind.Date, new DateOnly(2026, 10, 25), null, null),
            Assignee = "甲"
        };
        var result = InheritanceResolver.Resolve(none, parent,
            InheritField.All & ~InheritField.Due);
        Assert.Equal(DueSpec.Empty, result.Due);
        Assert.Equal("甲", result.Assignee);
    }

    [Fact]
    public void Independent_same_value_stays_independent_and_restore_inherit_follows_parent()
    {
        var own = new EffectiveFields("发布甲", "乙", Priority.High, null, DueSpec.Empty, null);
        var parent = new EffectiveFields("发布甲", "丙", Priority.Normal, null, DueSpec.Empty, null);

        var independentMask = InheritField.All & ~InheritField.Assignee;
        var resolvedIndependent = InheritanceResolver.Resolve(own, parent, independentMask);
        Assert.Equal("乙", resolvedIndependent.Assignee);

        var restoredMask = independentMask | InheritField.Assignee;
        var resolvedRestored = InheritanceResolver.Resolve(own, parent, restoredMask);
        Assert.Equal("丙", resolvedRestored.Assignee);
    }

    [Fact]
    public void Root_record_cannot_have_non_zero_inherit_mask()
    {
        var own = new EffectiveFields(null, null, Priority.Normal, null, DueSpec.Empty, null);
        Assert.Throws<ArgumentException>(() => InheritanceResolver.Resolve(own, null, InheritField.Assignee));
    }

    [Fact]
    public void RecordRules_validates_blank_content_and_invalid_surrogates()
    {
        Assert.False(RecordRules.HasValidText(" ", "\t\r\n"));
        Assert.True(RecordRules.HasValidText("标题", ""));
        Assert.False(RecordRules.HasValidText("含空字符\0", "正文"));
    }
}

using AiResume.Wrapper;
using Xunit;

namespace AiResume.Tests;

/// <summary>
/// S6-B 授权映射回归:feishuAuthOpenIds 三态(owner/viewer/none)、空名单=未锁定、
/// allow_from 生成与解锁警告语义。全部离线,无网络无凭据。
/// </summary>
public sealed class CcConnectAuthMapperTests
{
    private static readonly string[] Owners = { "ou_owner_1", "ou_owner_2" };
    private static readonly string[] Viewers = { "ou_viewer_1" };

    [Fact]
    public void Resolve_empty_lists_means_unlocked_with_warning()
    {
        // 现役语义:feishuAuthOpenIds 为空 = 未锁定,所有人可改,必须带警告。
        AuthDecision decision = CcConnectAuthMapper.Resolve("ou_anyone", Array.Empty<string>(), Array.Empty<string>());
        Assert.Equal(AccessLevel.Owner, decision.Level);
        Assert.True(decision.FileToolsAllowed);
        Assert.False(string.IsNullOrEmpty(decision.Warning));
    }

    [Fact]
    public void Resolve_owner_gets_full_access()
    {
        AuthDecision decision = CcConnectAuthMapper.Resolve("ou_owner_1", Owners, Viewers);
        Assert.Equal(AccessLevel.Owner, decision.Level);
        Assert.True(decision.FileToolsAllowed);
        Assert.Null(decision.Warning);
    }

    [Fact]
    public void Resolve_viewer_has_no_file_tools()
    {
        // 现役红线:非 owner 的查询/闲聊必须禁全部文件工具。
        AuthDecision decision = CcConnectAuthMapper.Resolve("ou_viewer_1", Owners, Viewers);
        Assert.Equal(AccessLevel.Viewer, decision.Level);
        Assert.False(decision.FileToolsAllowed);
    }

    [Fact]
    public void Resolve_unknown_user_gets_none()
    {
        AuthDecision decision = CcConnectAuthMapper.Resolve("ou_stranger", Owners, Viewers);
        Assert.Equal(AccessLevel.None, decision.Level);
        Assert.False(decision.FileToolsAllowed);
    }

    [Fact]
    public void Resolve_owner_wins_when_id_in_both_lists()
    {
        AuthDecision decision = CcConnectAuthMapper.Resolve("ou_dual", new[] { "ou_dual" }, new[] { "ou_dual" });
        Assert.Equal(AccessLevel.Owner, decision.Level);
        Assert.True(decision.FileToolsAllowed);
    }

    [Fact]
    public void Resolve_blank_open_id_never_matches()
    {
        Assert.Equal(AccessLevel.None, CcConnectAuthMapper.Resolve("", Owners, Viewers).Level);
        Assert.Equal(AccessLevel.None, CcConnectAuthMapper.Resolve("   ", Owners, Viewers).Level);
    }

    [Fact]
    public void Build_allow_from_is_union_ordered_and_deduped()
    {
        IReadOnlyList<string> allowFrom = CcConnectAuthMapper.BuildAllowFrom(
            new[] { "ou_a", "ou_b" }, new[] { "ou_b", "ou_c", " ", "" });
        Assert.Equal(new[] { "ou_a", "ou_b", "ou_c" }, allowFrom);
    }

    [Fact]
    public void Build_allow_from_empty_means_unset_allow_all()
    {
        // 空名单 = 不设置 allow_from(全允许,与未锁定语义一致)。
        Assert.Empty(CcConnectAuthMapper.BuildAllowFrom(Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Removing_last_owner_unlocks()
    {
        Assert.True(CcConnectAuthMapper.RemovingOwnerUnlocks(new[] { "ou_only" }, "ou_only"));
        Assert.False(CcConnectAuthMapper.RemovingOwnerUnlocks(new[] { "ou_a", "ou_b" }, "ou_a"));
        Assert.False(CcConnectAuthMapper.RemovingOwnerUnlocks(new[] { "ou_a" }, "ou_other"));
        Assert.False(CcConnectAuthMapper.RemovingOwnerUnlocks(Array.Empty<string>(), "ou_a"));
    }
}

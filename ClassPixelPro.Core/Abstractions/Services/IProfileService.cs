using ClassPixelPro.Shared.IPC.Abstractions.Services;

namespace ClassPixelPro.Core.Abstractions.Services;

/// <summary>
/// 档案服务，用于管理ClassPixelPro档案信息。
/// </summary>
public interface IProfileService : IPublicProfileService
{
    internal Task LoadProfileAsync();

    /// <summary>
    /// 从集控拉取课表/时间表/科目并合并到当前档案。
    /// 在集控定时刷新检测到 classplan/subjects/timelayout 有更新时调用。
    /// </summary>
    Task MergeManagementProfileAsync();

    /// <summary>
    /// 清空过期的临时层课表
    /// </summary>
    void CleanExpiredTempClassPlan();

    /// <summary>
    /// 清除过期的临时课表群。
    /// </summary>
    void ClearExpiredTempClassPlanGroup();

    /// <summary>
    /// 将当前档案标记为信任。
    /// </summary>
    void TrustCurrentProfile();
}
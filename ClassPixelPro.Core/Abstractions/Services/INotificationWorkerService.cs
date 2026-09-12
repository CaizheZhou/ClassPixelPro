using ClassPixelPro.Core.Models.Notification;

namespace ClassPixelPro.Core.Abstractions.Services;

/// <summary>
/// 提醒工作服务
/// </summary>
public interface INotificationWorkerService
{
    internal NotificationPlayingTicket CreateTicket(NotificationRequest request);
    
}
using System.Threading.Tasks;
using UltimateServer.Events;

namespace UltimateServer.Services
{
    /// <summary>
    /// A service to handle notifications, reacting to various domain events.
    /// </summary>
    public class NotificationService :
        IEventHandler<UserRegisteredEvent>,
        IEventHandler<VideoUploadedEvent>
    {
        private readonly Logger _logger;

        public NotificationService(Logger logger)
        {
            _logger = logger;
        }

        public async Task HandleAsync(UserRegisteredEvent eventData)
        {
            // In a real app, this might send a welcome email or a WebSocket notification.
            await Task.Run(() =>
            {
                _logger.Log($"🎉 Notification: New user '{eventData.User.Username}' (Email: {eventData.User.Email}) has registered! Welcome them.");
            });
        }

        public async Task HandleAsync(VideoUploadedEvent eventData)
        {
            // In a real app, this might notify admins or process the video (e.g., create thumbnails).
            await Task.Run(() =>
            {
                _logger.Log($"🎬 Notification: Video '{eventData.FileName}' from URL '{eventData.SourceUrl}' has been uploaded and is ready for processing.");
            });
        }
    }
}
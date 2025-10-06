using UltimateServer.Models;

namespace UltimateServer.Events
{
    /// <summary>
    /// Event published when a new user successfully registers.
    /// </summary>
    public class UserRegisteredEvent : BaseEvent
    {
        public User User { get; }

        public UserRegisteredEvent(User user)
        {
            User = user;
        }
    }
}
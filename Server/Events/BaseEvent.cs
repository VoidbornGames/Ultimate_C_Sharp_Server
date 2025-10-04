using System;
using UltimateServer.Services;

namespace UltimateServer.Events
{
    /// <summary>
    /// Base class for all events, providing common properties.
    /// </summary>
    public abstract class BaseEvent : IEvent
    {
        public Guid Id { get; } = Guid.NewGuid();
        public DateTime Timestamp { get; } = DateTime.UtcNow;
    }
}
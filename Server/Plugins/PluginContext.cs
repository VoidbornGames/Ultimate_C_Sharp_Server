// In UltimateServer.Services/PluginContext.cs

using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using UltimateServer.Plugins;
using UltimateServer.Services;

namespace UltimateServer.Services
{
    /// <summary>
    /// Implementation of IPluginContext
    /// </summary>
    public class PluginContext : IPluginContext
    {
        public Logger Logger { get; }
        public IEventBus EventBus { get; }
        public IServiceProvider ServiceProvider { get; }

        private readonly Dictionary<string, Func<HttpListenerRequest, Task>> _routes = new();

        // FIXED: Use the server's logger instance directly
        public PluginContext(Logger serverLogger, IEventBus eventBus, IServiceProvider serviceProvider)
        {
            Logger = serverLogger;
            EventBus = eventBus;
            ServiceProvider = serviceProvider;
        }

        public void RegisterApiRoute(string path, Func<HttpListenerRequest, Task> handler)
        {
            _routes[path] = handler;
            Logger.Log($"🔌 Plugin registered route: {path}");
        }

        public Func<HttpListenerRequest, Task> GetRouteHandler(string path)
        {
            return _routes.TryGetValue(path, out var handler) ? handler : null;
        }
    }
}
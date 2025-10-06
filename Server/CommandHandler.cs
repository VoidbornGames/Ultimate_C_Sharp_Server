using System;
using System.Diagnostics;
using Newtonsoft.Json;
using UltimateServer.Models;

namespace UltimateServer.Services
{
    public class CommandHandler
    {
        private readonly UserService _userService;
        private readonly Logger _logger;
        private readonly Dictionary<string, Func<Data, Data>> _commandHandlers;

        public CommandHandler(UserService userService, Logger logger)
        {
            _userService = userService;
            _logger = logger;
            _commandHandlers = new Dictionary<string, Func<Data, Data>>(StringComparer.OrdinalIgnoreCase);
            RegisterCommands();
        }

        private void RegisterCommands()
        {
            //AddCommand("createUser", CreateUserCommand);
            //AddCommand("loginUser", LoginUserCommand);
            AddCommand("listUsers", ListUsersCommand);
            AddCommand("say", SayCommand);
            AddCommand("makeUUID", MakeUUIDCommand);
            AddCommand("stats", StatsCommand);
        }

        private Data CreateUserCommand(Data req)
        {
            try
            {
                var registerRequest = JsonConvert.DeserializeObject<RegisterRequest>(req.jsonData);
                if (registerRequest == null)
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "Invalid request data." };
                }

                // Call the async method and bridge the sync/async gap
                var (user, message) = _userService.CreateUserAsync(registerRequest).GetAwaiter().GetResult();

                if (user != null)
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"User {user.Username} created." };
                }
                else
                {
                    return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = $"Error: {message}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in CreateUserCommand: {ex.Message}");
                return new Data { protocolVersion = 1, theCommand = "createUser", jsonData = "An unexpected error occurred." };
            }
        }

        private Data LoginUserCommand(Data req)
        {
            try
            {
                var loginRequest = JsonConvert.DeserializeObject<LoginRequest>(req.jsonData);
                if (loginRequest == null)
                {
                    return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = "Invalid request data." };
                }

                // Call the async method and bridge the sync/async gap
                var (user, message) = _userService.AuthenticateUserAsync(loginRequest).GetAwaiter().GetResult();

                if (user != null)
                {
                    _logger.Log($"✅ User Login: {user.Username}");
                    return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = "{ \"Succeed\": \"true\" }" };
                }
                else
                {
                    return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = $"Error: {message}" };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in LoginUserCommand: {ex.Message}");
                return new Data { protocolVersion = 1, theCommand = "loginUser", jsonData = "An unexpected error occurred." };
            }
        }

        private Data ListUsersCommand(Data req)
        {
            string usersJson = JsonConvert.SerializeObject(_userService.Users);
            _logger.Log("📄 Sent user list.");
            return new Data { protocolVersion = 1, theCommand = "listUsers", jsonData = usersJson };
        }

        private Data SayCommand(Data req)
        {
            _logger.Log($"🗨️ Client says: {req.jsonData}");
            return new Data { protocolVersion = 1, theCommand = "reply", jsonData = $"Server received: {req.jsonData}" };
        }

        private Data MakeUUIDCommand(Data req)
        {
            var uuid = Guid.NewGuid();
            _logger.Log($"🆔 Generated UUID: {uuid}");
            return new Data { protocolVersion = 1, theCommand = "uuid", jsonData = JsonConvert.SerializeObject(uuid) };
        }

        private Data StatsCommand(Data req)
        {
            var stats = new
            {
                uptime = (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(),
                users = _userService.Users.Count,
                protocol = 1
            };
            return new Data { protocolVersion = 1, theCommand = "stats", jsonData = JsonConvert.SerializeObject(stats) };
        }

        private void AddCommand(string name, Func<Data, Data> handler) => _commandHandlers[name] = handler;

        public bool TryHandleCommand(Data request, out Data response)
        {
            response = null;
            if (_commandHandlers.TryGetValue(request.theCommand, out var handler))
            {
                response = handler(request);
                return true;
            }
            return false;
        }
    }
}
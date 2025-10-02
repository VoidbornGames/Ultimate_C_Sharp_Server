<div class="container">
<h1 id="ultimateserver">UltimateServer</h1>
<p>A powerful, multi-purpose <strong>C# server</strong> built with a modern, service-oriented architecture. Features real-time dashboard, robust user management, and secure video streaming capabilities. Designed for game servers, monitoring systems, and real-time applications.</p>
<p>Check out the live dashboard here: <a href="https://dashboard.voidborn-games.ir" target="_blank"><strong>Live Dashboard</strong></a></p>
<a href="https://dashboard.voidborn-games.ir" target="_blank"><img src="https://voidborn-games.ir/wp-content/uploads/2025/10/Screenshot-10_1_2025-11_55_47-PM.png" alt="Dashboard Screenshot"></a>

<h2 id="table-of-contents">Table of Contents</h2>
<ul>
<li><a href="#features">Features</a></li>
<li><a href="#requirements">Requirements</a></li>
<li><a href="#installation">Installation</a></li>
<li><a href="#running-it">Running it</a></li>
<li><a href="#usage">Usage</a></li>
<li><a href="#api-endpoints">API Endpoints</a></li>
<li><a href="#configuration">Configuration</a></li>
<li><a href="#video-management">Video Management</a></li>
<li><a href="#logging">Logging</a></li>
<li><a href="#security-features">Security Features</a></li>
<li><a href="#troubleshooting">Troubleshooting</a></li>
<li><a href="#license">License</a></li>
<li><a href="#contributing">Contributing</a></li>
<li><a href="#support">Support</a></li>
</ul>

<h2 id="features">Features</h2>
<div class="feature-list">
<div class="feature-card">
<h3 id="-modern-architecture">🏗️ Modern Architecture</h3>
<ul>
<li><strong>Service-Oriented Design</strong>: Decoupled services for improved maintainability and testability.</li>
<li><strong>Dependency Injection</strong>: Clean, modular code with clear separation of concerns.</li>
<li><strong>Asynchronous Performance</strong>: Full <code>async/await</code> implementation for high concurrency and non-blocking I/O.</li>
<li><strong>Event-Driven Communication</strong>: A robust in-memory Event Bus for decoupled, scalable inter-service communication.</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-enhanced-security">🔒 Enhanced Security</h3>
<ul>
<li><strong>Advanced Password Hashing</strong>: Securely hashes passwords using PBKDF2 with unique salts.</li>
<li><strong>Account Lockout Policy</strong>: Automatically locks accounts after multiple failed login attempts.</li>
<li><strong>JWT & Refresh Tokens</strong>: Secure, stateless authentication with short-lived access tokens and long-lived refresh tokens.</li>
<li><strong>Comprehensive Input Validation</strong>: Protects against injection attacks and malicious data.</li>
<li><strong>Strong Password Policies</strong>: Enforces configurable password complexity requirements.</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-high-performance-core">🚀 High-Performance Core</h3>
<ul>
<li><strong>In-Memory Caching</strong>: Reduces database/file I/O for frequently accessed data.</li>
<li><strong>HTTP Response Compression</strong>: Automatically compresses responses to save bandwidth.</li>
<li><strong>Connection Pooling Framework</strong>: Efficiently manages and reuses network connections.</li>
<li><strong>Graceful Shutdown</strong>: Ensures data is saved and connections close properly on exit.</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-web-dashboard">🌐 Web Dashboard</h3>
<ul>
<li>Modern, responsive dark-themed interface</li>
<li>Real-time system monitoring (CPU, memory, disk, network)</li>
<li>Live server logs viewer with color-coded levels</li>
<li>Video player with streaming support and progress tracking</li>
<li>Secure user authentication with JWT tokens</li>
<li>Mobile-friendly design</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-advanced-user-management">👥 Advanced User Management</h3>
<ul>
<li>Secure user registration and login system</li>
<li>Role-based access control (RBAC)</li>
<li>Session management with "remember me" functionality</li>
<li>Password reset functionality via email token</li>
<li>Two-Factor Authentication (2FA) support framework</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-system-monitoring">📊 System Monitoring</h3>
<ul>
<li>Real-time CPU usage tracking</li>
<li>Memory consumption monitoring</li>
<li>Disk space usage information</li>
<li>Network traffic statistics</li>
<li>Historical performance charts</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-video-management">🎬 Video Management</h3>
<ul>
<li>Upload videos from URLs with progress tracking</li>
<li>Stream videos directly in the dashboard</li>
<li>Support for multiple video formats (MP4, WebM, OGG, AVI, MOV, MKV)</li>
<li>Video library with thumbnail previews</li>
<li>Secure access control for all video content</li>
</ul>
</div>

<div class="feature-card">
<h3 id="-logging-system">📝 Logging System</h3>
<ul>
<li>Automatic log rotation with ZIP compression</li>
<li>Real-time log viewing in dashboard</li>
<li>Multiple log levels (Info, Warning, Error, Security)</li>
<li>Persistent log storage</li>
</ul>
</div>
</div>

<h2 id="requirements">Requirements</h2>
<ul>
<li>.NET 6.0 SDK or Runtime</li>
<li>Windows, Linux, or Docker-compatible environment</li>
<li>Ports open for TCP connections (default: <code>11001</code> for server, <code>11002</code> for web dashboard)</li>
<li>Modern web browser with JavaScript enabled</li>
</ul>

<h2 id="installation">Installation</h2>
<ol>
<li><strong>Clone the repository</strong>:
<pre><code class="language-bash">git clone https://github.com/VoidbornGames/Ultimate_C_Sharp_Server.git
cd Ultimate_C_Sharp_Server</code></pre>
</li>
<li><strong>Build the project</strong>:
<pre><code class="language-bash">dotnet build</code></pre>
</li>
</ol>

<h2 id="running-it">Running it</h2>
<h3 id="default-configuration">Default Configuration:</h3>
<pre><code class="language-bash">dotnet Server.dll 11001 11002</code></pre>

<h3 id="custom-ports">Custom Ports:</h3>
<pre><code class="language-bash">dotnet Server.dll &lt;Server_Port&gt; &lt;Dashboard_Port&gt;</code></pre>

<h3 id="docker-recommended">Docker (Recommended):</h3>
<pre><code class="language-bash">docker build -t ultimateserver .
docker run -p 11001:11001 -p 11002:11002 ultimateserver</code></pre>

<h2 id="usage">Usage</h2>
<h3 id="accessing-the-dashboard">Accessing the Dashboard</h3>
<ol>
<li>Open your web browser and navigate to:
<pre><code>http://your-server-ip:11002</code></pre>
</li>
<li>Login with the default credentials:
<ul>
<li>Username: <code>admin</code></li>
<li>Password: <code>admin123</code></li>
</ul>
</li>
<li>Explore the dashboard features:
<ul>
<li><strong>Stats</strong>: View real-time system performance</li>
<li><strong>Logs</strong>: Monitor server activity</li>
<li><strong>Videos</strong>: Upload and stream videos</li>
</ul>
</li>
</ol>

<h3 id="default-commands">Default Commands</h3>
<p>The server supports several built-in commands that can be sent via TCP:</p>
<ul>
<li><code>createUser</code>: Create a new user account</li>
<li><code>loginUser</code>: Authenticate a user</li>
<li><code>listUsers</code>: Get a list of all users</li>
<li><code>say</code>: Echo a message back to the client</li>
<li><code>makeUUID</code>: Generate a unique identifier</li>
<li><code>stats</code>: Get server statistics</li>
</ul>

<h2 id="api-endpoints">API Endpoints</h2>
<h3 id="authentication">Authentication</h3>
<ul>
<li><code>POST /api/login</code>: Authenticate user and get JWT token
<pre><code class="language-json">{
"username": "admin",
"password": "admin123",
"rememberMe": true
}</code></pre>
</li>
<li><code>POST /api/register</code>: Register a new user
<pre><code class="language-json">{
"username": "newuser",
"email": "user@example.com",
"password": "StrongPassword123!",
"role": "player"
}</code></pre>
</li>
<li><code>POST /api/refresh-token</code>: Get a new access token using a refresh token</li>
<li><code>POST /api/logout</code>: Invalidate the user's refresh token</li>
</ul>

<h3 id="system-information">System Information</h3>
<ul>
<li><code>GET /stats</code>: Get basic server statistics (requires authentication)</li>
<li><code>GET /system</code>: Get detailed system performance data (requires authentication)</li>
<li><code>GET /logs</code>: Get recent server logs (requires authentication)</li>
</ul>

<h3 id="video-management-api">Video Management</h3>
<ul>
<li><code>GET /videos</code>: List all available videos (requires authentication)</li>
<li><code>POST /upload-url</code>: Upload a video from a URL (requires authentication)</li>
<li><code>GET /videos/{filename}</code>: Stream a video file (requires authentication)</li>
</ul>

<p>All protected endpoints require a valid JWT token in the Authorization header:</p>
<pre><code>Authorization: Bearer &lt;your-jwt-token&gt;</code></pre>

<h2 id="configuration">Configuration</h2>
<p>The server uses a <code>config.json</code> file for configuration:</p>
<pre><code class="language-json">{
  "Ip": "0.0.0.0",
  "MaxConnections": 50,
  "PasswordMinLength": 8,
  "RequireSpecialChars": true,
  "MaxFailedLoginAttempts": 5,
  "LockoutDurationMinutes": 30,
  "JwtExpiryHours": 24,
  "RefreshTokenDays": 7,
  "MaxRequestSizeMB": 100,
  "EnableCompression": true,
  "CacheExpiryMinutes": 15,
  "ConnectionPoolSize": 10
}</code></pre>

<h3 id="configuration-options">Configuration Options</h3>
<ul>
<li><strong>Ip</strong>: Server IP address to bind to (default: "0.0.0.0")</li>
<li><strong>MaxConnections</strong>: Maximum number of concurrent connections (default: 50)</li>
<li><strong>PasswordMinLength</strong>: Minimum required length for user passwords.</li>
<li><strong>RequireSpecialChars</strong>: Enforces special characters in passwords.</li>
<li><strong>MaxFailedLoginAttempts</strong>: Number of failed attempts before account lockout.</li>
<li><strong>LockoutDurationMinutes</strong>: Duration of the account lockout.</li>
<li><strong>JwtExpiryHours</strong>: Expiration time for JWT access tokens.</li>
<li><strong>RefreshTokenDays</strong>: Expiration time for refresh tokens.</li>
<li><strong>EnableCompression</strong>: Enables Gzip/Deflate compression for HTTP responses.</li>
<li><strong>CacheExpiryMinutes</strong>: Default expiry time for cached items.</li>
<li><strong>ConnectionPoolSize</strong>: The number of connections to keep in the pool.</li>
</ul>

<h2 id="video-management">Video Management</h2>
<h3 id="uploading-videos">Uploading Videos</h3>
<ol>
<li>Navigate to the <strong>Videos</strong> tab in the dashboard</li>
<li>Enter a video URL in the upload field</li>
<li>Click <strong>Download</strong> to upload the video to the server</li>
<li>The video will appear in your video library</li>
</ol>

<h3 id="supported-video-formats">Supported Video Formats</h3>
<ul>
<li>MP4 (video/mp4)</li>
<li>WebM (video/webm)</li>
<li>OGG (video/ogg)</li>
<li>AVI (video/x-msvideo)</li>
<li>MOV (video/quicktime)</li>
<li>MKV (video/x-matroska)</li>
</ul>

<h3 id="video-streaming">Video Streaming</h3>
<p>Videos are streamed directly from the server with support for:</p>
<ul>
<li>Range requests for efficient streaming</li>
<li>Proper MIME type handling</li>
<li>Authentication protection</li>
<li>Progress tracking during upload</li>
</ul>

<h2 id="logging">Logging</h2>
<p>The server maintains detailed logs in the <code>logs/</code> directory:</p>
<ul>
<li><strong>Log Rotation</strong>: Logs are automatically rotated and compressed when the server restarts</li>
<li><strong>Log Levels</strong>: Information, warnings, errors, and security events are logged with timestamps</li>
<li><strong>Real-time Viewing</strong>: View live logs in the dashboard without needing to access the server files</li>
</ul>
<p>Log files are named with timestamps:</p>
<pre><code>logs/latest_2025-10-01_23-30-45.zip</code></pre>

<h2 id="security-features">Security Features</h2>
<ul>
<li><strong>PBKDF2 Password Hashing</strong>: User passwords are securely hashed with a unique salt.</li>
<li><strong>Account Lockout</strong>: Protects against brute-force attacks.</li>
<li><strong>JWT & Refresh Token Authentication</strong>: Secure, stateless session management.</li>
<li><strong>Input Validation</strong>: All inputs are validated to prevent injection attacks.</li>
<li><strong>File Access Control</strong>: Video files are protected with authentication checks.</li>
<li><strong>CORS Support</strong>: Properly configured Cross-Origin Resource Sharing headers.</li>
</ul>

<h2 id="troubleshooting">Troubleshooting</h2>
<h3 id="server-startup-issues">Server Startup Issues</h3>
<p>If the server fails to start, it's often due to a dependency injection configuration error.</p>
<ul>
<li><strong>Check Logs</strong>: The console output will show the exact error message, such as "Unable to resolve service for type 'System.String'".</li>
<li><strong>Verify Packages</strong>: Ensure the <code>Microsoft.Extensions.DependencyInjection</code> NuGet package is installed.</li>
<li><strong>Check Program.cs</strong>: Make sure all services (like `FilePaths`, `ServerSettings`) are registered in the DI container.</li>
</ul>

<h3 id="videos-not-loading">Videos Not Loading</h3>
<ol>
<li>Check that the video file exists on the server</li>
<li>Verify the video format is supported</li>
<li>Check browser console for error messages</li>
<li>Ensure your authentication token is valid</li>
</ol>

<h3 id="login-issues">Login Issues</h3>
<ol>
<li>Verify the default credentials: admin/admin123</li>
<li>Check that your browser allows cookies</li>
<li>Clear browser cache and try again</li>
<li>Check server logs for authentication errors</li>
</ol>

<h3 id="performance-issues">Performance Issues</h3>
<ol>
<li>Monitor CPU and memory usage in the dashboard</li>
<li>Check the number of active connections</li>
<li>Review logs for any error messages</li>
<li>Consider increasing <code>MaxConnections</code> or <code>ConnectionPoolSize</code> if needed</li>
</ol>

<h2 id="license">License</h2>
<p>This project is licensed under the MIT License - see the <a href="LICENSE">LICENSE</a> file for details.</p>

<h2 id="contributing">Contributing</h2>
<p>Contributions are welcome! Please feel free to submit a Pull Request.</p>
<ol>
<li>Fork the project</li>
<li>Create your feature branch (<code>git checkout -b feature/AmazingFeature</code>)</li>
<li>Commit your changes (<code>git commit -m 'Add some AmazingFeature'</code>)</li>
<li>Push to the branch (<code>git push origin feature/AmazingFeature</code>)</li>
<li>Open a Pull Request</li>
</ol>

<h2 id="support">Support</h2>
<p>If you encounter any issues or have questions, please:</p>
<ol>
<li>Check the <a href="https://github.com/VoidbornGames/Ultimate_C_Sharp_Server/issues">Issues</a> page</li>
<li>Create a new issue with detailed information</li>
<li>Include server logs and error messages when applicable</li>
</ol>

<div class="footer">
<p><strong>© 2025 UltimateServer · VoidbornGames</strong></p>
</div>
</div>

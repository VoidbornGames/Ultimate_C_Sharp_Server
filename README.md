<h1 id="ultimateserver">UltimateServer</h1>
<p>A powerful, multi-purpose <strong>C# server</strong> with real-time dashboard, user management, and video streaming capabilities. Designed for game servers, monitoring systems, and real-time applications.</p>
<p>Check out the live dashboard here: <a href="https://dashboard.voidborn-games.ir"><strong>Live Dashboard</strong></a></p>
<p><a href="https://dashboard.voidborn-games.ir" target="_blank"><img src="https://voidborn-games.ir/wp-content/uploads/2025/10/Screenshot-10_1_2025-11_55_47-PM.png" alt="Dashboard Screenshot"></a></p>
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
<li><a href="#license">License</a></li>
</ul>
<h2 id="features">Features</h2>
<h3 id="-web-dashboard">🌐 Web Dashboard</h3>
<ul>
<li>Modern, responsive dark-themed interface</li>
<li>Real-time system monitoring (CPU, memory, disk, network)</li>
<li>Live server logs viewer</li>
<li>Video player with streaming support</li>
<li>User authentication with JWT tokens</li>
<li>Mobile-friendly design</li>
</ul>
<h3 id="-server-core">🚀 Server Core</h3>
<ul>
<li>High-performance TCP server with async client handling</li>
<li>Multi-client concurrency support</li>
<li>Configurable IP, ports, and max connections</li>
<li>Automatic resource management</li>
</ul>
<h3 id="-user-management">👥 User Management</h3>
<ul>
<li>Secure user authentication system</li>
<li>Role-based access control</li>
<li>Session management with remember me functionality</li>
<li>Password strength indicators</li>
</ul>
<h3 id="-system-monitoring">📊 System Monitoring</h3>
<ul>
<li>Real-time CPU usage tracking</li>
<li>Memory consumption monitoring</li>
<li>Disk space usage information</li>
<li>Network traffic statistics</li>
<li>Historical performance charts</li>
</ul>
<h3 id="-video-management">🎬 Video Management</h3>
<ul>
<li>Upload videos from URLs</li>
<li>Stream videos directly in the dashboard</li>
<li>Support for multiple video formats (MP4, WebM, OGG, AVI, MOV, MKV)</li>
<li>Video library with thumbnail previews</li>
</ul>
<h3 id="-logging-system">📝 Logging System</h3>
<ul>
<li>Automatic log rotation with ZIP compression</li>
<li>Real-time log viewing in dashboard</li>
<li>Configurable log levels</li>
<li>Persistent log storage</li>
</ul>
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
cd Ultimate_C_Sharp_Server
</code></pre>
</li>
<li><strong>Build the project</strong>:
<pre><code class="language-bash">dotnet build
</code></pre>
</li>
</ol>
<h2 id="running-it">Running it</h2>
<h3 id="default-configuration">Default Configuration:</h3>
<pre><code class="language-bash">dotnet Server.dll 11001 11002
</code></pre>
<h3 id="custom-ports">Custom Ports:</h3>
<pre><code class="language-bash">dotnet Server.dll &lt;Server_Port&gt; &lt;Dashboard_Port&gt;
</code></pre>
<h3 id="docker-recommended">Docker (Recommended):</h3>
<pre><code class="language-bash">docker build -t ultimateserver .
docker run -p 11001:11001 -p 11002:11002 ultimateserver
</code></pre>
<h2 id="usage">Usage</h2>
<h3 id="accessing-the-dashboard">Accessing the Dashboard</h3>
<ol>
<li>Open your web browser and navigate to:
<pre><code>http://your-server-ip:11002
</code></pre>
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
<li><code>POST /api/login</code>: Authenticate user and get JWT token</li>
</ul>
<h3 id="system-information">System Information</h3>
<ul>
<li><code>GET /stats</code>: Get basic server statistics</li>
<li><code>GET /system</code>: Get detailed system performance data</li>
<li><code>GET /logs</code>: Get recent server logs</li>
</ul>
<h3 id="video-management">Video Management</h3>
<ul>
<li><code>GET /videos</code>: List all available videos</li>
<li><code>POST /upload-url</code>: Upload a video from a URL</li>
<li><code>GET /videos/{filename}</code>: Stream a video file (requires authentication)</li>
</ul>
<p>All protected endpoints require a valid JWT token in the Authorization header:</p>
<pre><code>Authorization: Bearer &lt;your-jwt-token&gt;
</code></pre>
<h2 id="configuration">Configuration</h2>
<p>The server uses a <code>config.json</code> file for configuration:</p>
<pre><code class="language-json">{
  "Ip": "0.0.0.0",
  "MaxConnections": 50,
  "DashboardPasswordHash": "12345678"
}
</code></pre>
<h3 id="configuration-options">Configuration Options</h3>
<ul>
<li><strong>Ip</strong>: Server IP address to bind to (default: "0.0.0.0")</li>
<li><strong>MaxConnections</strong>: Maximum number of concurrent connections (default: 50)</li>
<li><strong>DashboardPasswordHash</strong>: Legacy password hash for dashboard (not used with JWT)</li>
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
<li><strong>Log Levels</strong>: Information, warnings, and errors are logged with timestamps</li>
<li><strong>Real-time Viewing</strong>: View live logs in the dashboard without needing to access the server files</li>
</ul>
<p>Log files are named with timestamps:</p>
<pre><code>logs/latest_2025-10-01_23-30-45.zip
</code></pre>
<h2 id="security-features">Security Features</h2>
<ul>
<li><strong>JWT Authentication</strong>: Secure token-based authentication for the dashboard</li>
<li><strong>Password Hashing</strong>: User passwords are securely hashed using SHA256</li>
<li><strong>Input Validation</strong>: All inputs are validated to prevent injection attacks</li>
<li><strong>File Access Control</strong>: Video files are protected with authentication checks</li>
<li><strong>CORS Support</strong>: Properly configured Cross-Origin Resource Sharing headers</li>
</ul>
<h2 id="troubleshooting">Troubleshooting</h2>
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
<li>Consider increasing MaxConnections if needed</li>
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
<p><strong>© 2025 UltimateServer · VoidbornGames</strong></p>
    </article>

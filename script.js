
        // =============================================================================
        // BACKEND CONFIGURATION & API COMMUNICATION
        // =============================================================================

        const CONFIG = {
            get baseUrl() {
                return `${window.location.protocol}//${window.location.hostname}${window.location.port ? ':' + window.location.port : ''}`;
            }
        };

        // =============================================================================
        // AUTHENTICATION STATE MANAGEMENT
        // =============================================================================

        class AuthManager {
            static isAuthenticated() {
                const token = localStorage.getItem('authToken') || sessionStorage.getItem('authToken');
                return !!token;
            }

            static clearAuth() {
                localStorage.removeItem('authToken');
                localStorage.removeItem('refreshToken');
                sessionStorage.removeItem('authToken');
                sessionStorage.removeItem('refreshToken');
                localStorage.removeItem('user');
                sessionStorage.removeItem('user');
            }

            static setAuth(token, refreshToken, user, remember = false) {
                const storage = remember ? localStorage : sessionStorage;
                storage.setItem('authToken', token);
                storage.setItem('refreshToken', refreshToken);
                storage.setItem('user', JSON.stringify(user));
            }

            static getAuth() {
                const token = localStorage.getItem('authToken') || sessionStorage.getItem('authToken');
                const refreshToken = localStorage.getItem('refreshToken') || sessionStorage.getItem('refreshToken');
                const userStr = localStorage.getItem('user') || sessionStorage.getItem('user');

                let user = null;
                if (userStr) {
                    try {
                       user = JSON.parse(userStr);
                    } catch (e) {
                        console.error('Failed to parse user data from storage:', e);
                        this.clearAuth();
                    }
                }

                return { token, refreshToken, user };
            }

            static async refreshAuthToken() {
                const auth = this.getAuth();
                if (!auth.refreshToken) {
                    throw new Error('No refresh token available');
                }

                try {
                    const response = await fetch(`${CONFIG.baseUrl}/api/refresh-token`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ refreshToken: auth.refreshToken })
                    });

                    if (!response.ok) {
                        throw new Error('Token refresh failed');
                    }

                    const data = await response.json();
                    if (data.success) {
                        const remember = localStorage.getItem('authToken') !== null;
                        this.setAuth(data.token, data.refreshToken || auth.refreshToken, auth.user, remember);
                        return data.token;
                    } else {
                        throw new Error(data.message || 'Token refresh failed');
                    }
                } catch (error) {
                    console.error('Error refreshing token:', error);
                    this.clearAuth();
                    throw error;
                }
            }
        }

        // =============================================================================
        // API REQUEST HELPER WITH AUTOMATIC 
        // =============================================================================


        // =============================================================================
// MARKETPLACE FUNCTIONALITY
// =============================================================================

async function fetchMarketPlugins() {
    const marketPluginsList = document.getElementById('marketPluginsList');
    marketPluginsList.innerHTML = `
        <div class="empty-plugins">
            <i class="fas fa-download"></i>
            <h3>Loading Marketplace...</h3>
            <p>Please wait while we fetch the latest plugins.</p>
        </div>
    `;

    try {
        // FIX: Use the full absolute URL instead of relative path
        const response = await apiRequest('https://dashboard.voidgames.ir/api/marketplace');
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }
        
        // FIX: The API returns an object, so we need to get the 'plugins' array from it.
        const data = await response.json();
        const plugins = data.plugins;

        if (!plugins || plugins.length === 0) {
            marketPluginsList.innerHTML = `
                <div class="empty-plugins">
                    <i class="fas fa-store-slash"></i>
                    <h3>No Plugins Available</h3>
                    <p>The marketplace is currently empty. Check back later!</p>
                </div>
            `;
            return;
        }

        marketPluginsList.innerHTML = ''; // Clear loading message
        plugins.forEach(plugin => {
            const pluginCard = document.createElement('div');
            pluginCard.className = 'market-plugin-card';
            pluginCard.innerHTML = `
                <div class="market-plugin-info">
                    <h4>${plugin.Name}</h4>
                    <p>${plugin.Description}</p>
                </div>
                <div class="market-plugin-actions">
                    <button class="install-btn" onclick="installMarketPlugin('${encodeURIComponent(JSON.stringify(plugin))}')">
                        <i class="fas fa-download"></i> Install
                    </button>
                </div>
            `;
            marketPluginsList.appendChild(pluginCard);
        });

    } catch (error) {
        console.error('Error fetching market plugins:', error);
        marketPluginsList.innerHTML = `
            <div class="plugin-result error show" style="margin-top: 20px;">
                <i class="fas fa-exclamation-circle"></i> Failed to load marketplace plugins. ${error.message}
            </div>
        `;
    }
}



window.installMarketPlugin = async function (encodedPlugin) {
    let plugin;
    try {
        // Decode the JSON string passed from the onclick handler
        plugin = JSON.parse(decodeURIComponent(encodedPlugin));
    } catch (e) {
        console.error("Failed to parse plugin data:", e);
        showToast('Installation Failed', 'Invalid plugin data.', 'error');
        return;
    }

    // Find the button that was clicked to disable it and show loading
    const installBtn = event.target.closest('.install-btn');
    const originalBtnHtml = installBtn.innerHTML;
    installBtn.disabled = true;
    installBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Installing...';

    try {
        const response = await apiRequest('/api/marketplace/download', {
            method: 'POST',
            body: JSON.stringify(plugin) // Send the full plugin object
        });

        const result = await response.json();

        if (response.ok && result.success) {
            showToast('Installation Successful', `Plugin "${plugin.Name}" has been installed.`, 'success');
            // Change button to a success state
            installBtn.innerHTML = '<i class="fas fa-check"></i> Installed';
            installBtn.style.background = 'var(--text-dim)';
            installBtn.disabled = true;
        } else {
            // Restore button on failure
            installBtn.disabled = false;
            installBtn.innerHTML = originalBtnHtml;
            showToast('Installation Failed', result.message || `Could not install "${plugin.Name}".`, 'error');
        }
    } catch (error) {
        console.error('Error installing plugin:', error);
        // Restore button on error
        installBtn.disabled = false;
        installBtn.innerHTML = originalBtnHtml;
        showToast('Installation Failed', `An error occurred: ${error.message}`, 'error');
    }
};






        async function apiRequest(url, options = {}) {
            const auth = AuthManager.getAuth();

            options.headers = {
                'Content-Type': 'application/json',
                ...options.headers
            };

            if (auth.token) {
                options.headers['Authorization'] = `Bearer ${auth.token}`;
            }

            try {
                let response = await fetch(url, options);

                if (response.status === 401 && auth.refreshToken) {
                    try {
                        console.log('Access token expired, refreshing...');
                        const newToken = await AuthManager.refreshAuthToken();
                        options.headers['Authorization'] = `Bearer ${newToken}`;
                        response = await fetch(url, options);
                    } catch (refreshError) {
                        console.error('Token refresh failed:', refreshError);
                        showSessionExpiredModal();
                        throw refreshError;
                    }
                }

                return response;
            } catch (error) {
                console.error('API request error:', error);
                throw error;
            }
        }

        // =============================================================================
        // UI HELPERS (TOAST NOTIFICATIONS, MODALS)
        // =============================================================================

        function showToast(title, message, type = 'info') {
            const toast = document.getElementById('toast');
            const toastTitle = document.getElementById('toastTitle');
            const toastMessage = document.getElementById('toastMessage');
            const toastIcon = toast.querySelector('i');

            toastTitle.textContent = title;
            toastMessage.textContent = message;

            toast.className = 'toast toast-' + type;
            if (type === 'success') toastIcon.className = 'fas fa-check-circle';
            else if (type === 'error') toastIcon.className = 'fas fa-exclamation-circle';
            else if (type === 'warning') toastIcon.className = 'fas fa-exclamation-triangle';
            else toastIcon.className = 'fas fa-info-circle';

            toast.classList.add('show');

            setTimeout(() => toast.classList.remove('show'), 5000);
        }

        document.getElementById('toastClose').addEventListener('click', () => {
            document.getElementById('toast').classList.remove('show');
        });

        function showSessionExpiredModal() {
            document.getElementById('sessionModal').style.display = 'flex';
        }

        // =============================================================================
        // APPLICATION INITIALIZATION AND LIFECYCLE
        // =============================================================================

        document.addEventListener('DOMContentLoaded', function () {
            checkForResetToken();

            if (!document.getElementById('resetToken').value && AuthManager.isAuthenticated()) {
                showDashboard();
            } else if (!document.getElementById('resetToken').value) {
                showLogin();
            }
        });

        function showLogin() {
            document.getElementById('loginPage').style.display = 'flex';
            document.getElementById('dashboardPage').style.display = 'none';

            const loginForm = document.getElementById('loginForm');
            const loginBtn = document.getElementById('loginBtn');
            const loginBtnText = document.getElementById('loginBtnText');
            const loginSpinner = document.getElementById('loginSpinner');
            const errorMessage = document.getElementById('errorMessage');
            const successMessage = document.getElementById('successMessage');
            const passwordInput = document.getElementById('password');
            const passwordStrength = document.getElementById('passwordStrength');

            passwordInput.addEventListener('input', function () {
                const password = this.value;
                let strength = 0;
                if (password.length >= 8) strength++;
                if (password.match(/[a-z]+/)) strength++;
                if (password.match(/[A-Z]+/)) strength++;
                if (password.match(/[0-9]+/)) strength++;
                if (password.match(/[$@#&!]+/)) strength++;

                passwordStrength.className = 'password-strength-bar';
                if (strength <= 2) passwordStrength.classList.add('strength-weak');
                else if (strength === 3 || strength === 4) passwordStrength.classList.add('strength-medium');
                else passwordStrength.classList.add('strength-strong');
            });

            loginForm.addEventListener('submit', async function (e) {
                e.preventDefault();

                const username = document.getElementById('username').value;
                const password = document.getElementById('password').value;
                const remember = document.getElementById('remember').checked;

                loginBtn.disabled = true;
                loginBtnText.textContent = 'Signing In...';
                loginSpinner.style.display = 'inline-block';
                errorMessage.style.display = 'none';
                successMessage.style.display = 'none';

                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/login`, {
                        method: 'POST',
                        body: JSON.stringify({ username, password, remember })
                    });

                    const data = await response.json();

                    if (data.success) {
                        AuthManager.setAuth(data.token, data.refreshToken, data.user, remember);

                        successMessage.textContent = 'Login successful! Redirecting to dashboard...';
                        successMessage.style.display = 'block';

                        setTimeout(() => showDashboard(), 1000);
                    } else {
                        errorMessage.textContent = data.message || 'Invalid username or password';                        errorMessage.style.display = 'block';
                    }
                } catch (error) {
                    console.error('Login error:', error);
                    errorMessage.textContent = 'An error occurred. Please try again.';
                    errorMessage.style.display = 'block';
                } finally {
                    loginBtn.disabled = false;
                    loginBtnText.textContent = 'Sign In';
                    loginSpinner.style.display = 'none';
                }
            });

            document.getElementById('forgotPasswordLink').addEventListener('click', function (e) {
                e.preventDefault();
                loginForm.style.display = 'none';
                document.getElementById('requestResetForm').style.display = 'block';
                document.getElementById('confirmResetForm').style.display = 'none';
            });
        }

        function showDashboard() {
            document.getElementById('loginPage').style.display = 'none';
            document.getElementById('dashboardPage').style.display = 'flex';

            const auth = AuthManager.getAuth();
            if (auth.user) {
                document.getElementById('userName').textContent = auth.user.username || 'User';
                document.getElementById('userAvatar').textContent = (auth.user.username || 'U').charAt(0).toUpperCase();
            }

            const userInfo = document.getElementById('userInfo');
            const userDropdown = document.getElementById('userDropdown');
            userInfo.addEventListener('click', () => userDropdown.classList.toggle('show'));
            document.addEventListener('click', (e) => { if (!userInfo.contains(e.target)) userDropdown.classList.remove('show'); });

            document.getElementById('logoutBtn').addEventListener('click', async (e) => {
                e.preventDefault();
                await logout();
            });

            initializeDashboard();
        }

        async function logout() {
            try {
                await apiRequest(`${CONFIG.baseUrl}/api/logout`, { method: 'POST' });
            } catch (error) {
                console.error('Logout API call failed:', error);
            } finally {
                AuthManager.clearAuth();
                showLogin();
                showToast('Logged Out', 'You have been successfully logged out', 'info');
            }
        }

        function redirectToLogin() {
            logout();
        }

        // =============================================================================
        // PASSWORD RESET FUNCTIONALITY
        // =============================================================================

        const loginForm = document.getElementById('loginForm');
        const requestResetForm = document.getElementById('requestResetForm');
        const confirmResetForm = document.getElementById('confirmResetForm');

        const backToLoginLink = document.getElementById('backToLoginLink');

        backToLoginLink.addEventListener('click', (e) => {
            e.preventDefault();
            loginForm.style.display = 'block';
            requestResetForm.style.display = 'none';
            confirmResetForm.style.display = 'none';
        });

        document.getElementById('resetRequestForm').addEventListener('submit', async function (e) {
            e.preventDefault();
            const email = document.getElementById('resetEmail').value;
            const btn = document.getElementById('resetRequestBtn');
            const btnText = document.getElementById('resetRequestBtnText');
            const spinner = document.getElementById('resetRequestSpinner');
            const errorMsg = document.getElementById('resetErrorMessage');
            const successMsg = document.getElementById('resetSuccessMessage');

            btn.disabled = true;
            btnText.textContent = 'Sending...';
            spinner.style.display = 'inline-block';
            errorMsg.style.display = 'none';
            successMsg.style.display = 'none';

            try {
                const response = await apiRequest(`${CONFIG.baseUrl}/api/request-password-reset`, {
                    method: 'POST',
                    body: JSON.stringify({ email })
                });
                const data = await response.json();

                if (data.success) {
                    successMsg.textContent = data.message;
                    successMsg.style.display = 'block';
                    document.getElementById('resetEmail').value = '';
                } else {
                    errorMsg.textContent = data.message;
                    errorMsg.style.display = 'block';
                }
            } catch (error) {
                console.error('Reset request error:', error);
                errorMsg.textContent = 'An error occurred. Please try again.';
                errorMsg.style.display = 'block';
            } finally {
                btn.disabled = false;
                btnText.textContent = 'Send Reset Link';
                spinner.style.display = 'none';
            }
        });

        document.getElementById('resetConfirmForm').addEventListener('submit', async function (e) {
            e.preventDefault();
            const newPassword = document.getElementById('newPassword').value;
            const confirmPassword = document.getElementById('confirmNewPassword').value;
            const token = document.getElementById('resetToken').value;
            const email = document.getElementById('resetEmail').value;

            if (newPassword !== confirmPassword) {
                document.getElementById('confirmErrorMessage').textContent = 'Passwords do not match.';
                document.getElementById('confirmErrorMessage').style.display = 'block';
                return;
            }

            const btn = document.getElementById('resetConfirmBtn');
            const btnText = document.getElementById('resetConfirmBtnText');
            const spinner = document.getElementById('resetConfirmSpinner');
            const errorMsg = document.getElementById('confirmErrorMessage');
            const successMsg = document.getElementById('confirmSuccessMessage');

            btn.disabled = true;
            btnText.textContent = 'Resetting...';
            spinner.style.display = 'inline-block';
            errorMsg.style.display = 'none';
            successMsg.style.display = 'none';

            try {
                const response = await apiRequest(`${CONFIG.baseUrl}/api/confirm-password-reset`, {
                    method: 'POST',
                    body: JSON.stringify({ email, token, newPassword })
                });
                const data = await response.json();

                if (data.success) {
                    successMsg.textContent = data.message;
                    successMsg.style.display = 'block';
                    setTimeout(() => {
                        loginForm.style.display = 'block';
                        confirmResetForm.style.display = 'none';
                        showToast('Success', data.message, 'success');
                    }, 2000);
                } else {
                    errorMsg.textContent = data.message;
                    errorMsg.style.display = 'block';
                }
            } catch (error) {
                console.error('Reset confirmation error:', error);
                errorMsg.textContent = 'An error occurred. Please try again.';
                errorMsg.style.display = 'block';
            } finally {
                btn.disabled = false;
                btnText.extContent = 'Reset Password';
                spinner.style.display = 'none';
            }
        });

        document.getElementById('newPassword').addEventListener('input', function () {
            const password = this.value;
            let strength = 0;
            if (password.length >= 8) strength++;
            if (password.match(/[a-z]+/)) strength++;
            if (password.match(/[A-Z]+/)) strength++;
            if (password.match(/[0-9]+/)) strength++;
            if (password.match(/[$@#&!]+/)) strength++;

            const strengthBar = document.getElementById('newPasswordStrength');
            strengthBar.className = 'password-strength-bar';
            if (strength <= 2) strengthBar.classList.add('strength-weak');
            else if (strength === 3 || strength === 4) strengthBar.classList.add('strength-medium');
            else strengthBar.classList.add('strength-strong');
        });

        function checkForResetToken() {
            const urlParams = new URLSearchParams(window.location.search);
            const token = urlParams.get('token');
            const email = urlParams.get('email');

            if (token && email) {
                loginForm.style.display = 'none';
                requestResetForm.style.display = 'none';
                confirmResetForm.style.display = 'block';

                document.getElementById('resetToken').value = token;
                document.getElementById('resetEmail').value = email;
            }
        }

        // =============================================================================
        // DASHBOARD FUNCTIONALITY
        // =============================================================================

        function initializeDashboard() {
            const tabs = document.querySelectorAll('.nav-item');
            const tabContents = document.querySelectorAll('.tab-content');
            function activateTab(name) {
                tabs.forEach(t => t.classList.remove('active'));
                tabContents.forEach(c => c.classList.remove('active'));
                const tab = document.querySelector(`.nav-item[data-tab="${name}"]`);
                const content = document.getElementById(name);
                if (tab && content) { tab.classList.add('active'); content.classList.add('active'); }

                if (name === 'pluginsTab') {
                    fetchPlugins();
                } else if (name === 'sitesTab') {
                    fetchSites();
                } else if (name === 'marketTab') { // <-- ADD THIS ELSE IF BLOCK
            fetchMarketPlugins();
        }
            }
            tabs.forEach(tab => tab.addEventListener('click', () => activateTab(tab.dataset.tab)));

            function updateTime() {
                const now = new Date();
                document.getElementById('currentTime').textContent = now.toLocaleTimeString();
            }
            updateTime();
            setInterval(updateTime, 1000);

            const maxDataPoints = 20;
            const cpuData = [], memoryData = [], chartLabels = [];
            const cpuChart = new Chart(document.getElementById('cpuChart').getContext('2d'), {
                type: 'line',
                data: {
                    labels: chartLabels,
                    datasets: [{
                        label: 'CPU Usage (%)',
                        data: cpuData,
                        borderColor: '#6366f1',
                        backgroundColor: 'rgba(99, 102, 241, 0.1)',
                        tension: 0.4,
                        fill: true
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    scales: {
                        y: {
                            beginAtZero: true,
                            max: 100,
                            grid: { color: 'rgba(255, 255, 255, 0.1)' },
                            ticks: { color: '#94a3b8' }
                        },
                        x: {
                            grid: { color: 'rgba(255, 255, 255, 0.1)' },
                            ticks: { color: '#94a3b8' }
                        }
                    },
                    plugins: {
                        legend: { display: false }
                    }
                }
            });

            const memoryChart = new Chart(document.getElementById('memoryChart').getContext('2d'), {
                type: 'line',
                data: {
                    labels: chartLabels,
                    datasets: [{
                        label: 'Memory Usage (MB)',
                        data: memoryData,
                        borderColor: '#10b981',
                        backgroundColor: 'rgba(16, 185, 129, 0.1)',
                        tension: 0.4,
                        fill: true
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    scales: {
                        y: {
                            beginAtZero: true,
                            grid: { color: 'rgba(255, 255, 255, 0.1)' },
                            ticks: { color: '#94a3b8' }
                        },
                        x: {
                            grid: { color: 'rgba(255, 255, 255, 0.1)' },
                            ticks: { color: '#94a3b8' }
                        }
                    },
                    plugins: {
                        legend: { display: false }
                    }
                }
            });

            function updateCharts(cpu, memory) {
                const now = new Date().toLocaleTimeString();
                chartLabels.push(now);
                cpuData.push(cpu);
                memoryData.push(memory);
                if (chartLabels.length > maxDataPoints) {
                    chartLabels.shift();
                    cpuData.shift();
                    memoryData.shift();
                }
                cpuChart.update('none');
                memoryChart.update('none');
            }

            async function fetchStats() {
                try {
                    const data = await apiRequest(`${CONFIG.baseUrl}/stats`).then(r => r.json());
                    document.getElementById("uptime").textContent = data.uptime || "N/A";
                    document.getElementById("onlineUsers").textContent = data.users || "0";
                    document.getElementById("maxConnections").textContent = data.maxConnections || "0";
                } catch (error) { console.error('Error fetching stats:', error); }
            }

            async function fetchSystem() {
                try {
                    const data = await apiRequest(`${CONFIG.baseUrl}/system`).then(r => r.json());
                    const cpuUsage = (data.cpuUsage || 0).toFixed(2);
                    const memoryUsage = (data.memoryMB || 0).toFixed(2);
                    document.getElementById("cpuUsage").textContent = cpuUsage + "%";
                    document.getElementById("memoryUsage").textContent = memoryUsage + " MB";
                    document.getElementById("diskUsage").textContent = `${(data.diskUsedGB || 0).toFixed(2)} / ${(data.diskTotalGB || 0).toFixed(2)} GB`;
                    document.getElementById("networkUsageD").textContent = `${(data.netReceivedMB || 0).toFixed(2)} MB ↓`;
                    document.getElementById("networkUsageU").textContent = `${(data.netSentMB || 0).toFixed(2)} MB ↑`;
                    updateCharts(parseFloat(cpuUsage), parseFloat(memoryUsage));
                } catch (error) { console.error('Error fetching system data:', error); }
            }
            
            let lastLogCount = 0;
            async function fetchLogs() {
    try {
        const logs = await apiRequest(`${CONFIG.baseUrl}/logs`).then(r => r.json());
        const logsEl = document.getElementById("serverLogs");
        
        // Only process new logs
        const newLogs = logs.slice(lastLogCount);
        lastLogCount = logs.length;
        
        // Add new logs to the container
        newLogs.forEach(l => {
            let className = 'log-info';            if (l.includes('[ERROR]')) className = 'log-error';
            else if (l.includes('[WARNING]')) className = 'log-warning';
            else if (l.includes('[SECURITY]')) className = 'log-security';
            
            const logEntry = document.createElement('div');
            logEntry.className = className;
            logEntry.textContent = l;
            logsEl.appendChild(logEntry);
        });
        
        // Auto-scroll to bottom if user hasn't scrolled up
        if (logsEl.scrollTop + logsEl.clientHeight >= logsEl.scrollHeight - 50) {
            logsEl.scrollTop = logsEl.scrollHeight;
        }
        
        // Limit logs to prevent memory issues
        while (logsEl.children.length > 500) {
            logsEl.removeChild(logsEl.firstChild);
        }
    } catch (error) { 
        console.error('Error fetching logs:', error); 
    }
}

            async function fetchVideos() {
                try {
                    const videos = await apiRequest(`${CONFIG.baseUrl}/videos`).then(r => r.json());
                    const videoList = document.getElementById("videoList");
                    if (!videos || videos.length === 0) {
                        videoList.innerHTML = "<p style='color: var(--text-dim); text-align: center; grid-column: 1/-1; padding: 20px;'>No videos uploaded yet.</p>";
                        return;
                    }
                    videoList.innerHTML = "";
                    videos.forEach(v => {
                        const div = document.createElement("div"); div.className = "video-card";
                        const thumbnail = document.createElement("div"); thumbnail.className = "video-thumbnail";
                        thumbnail.style.backgroundImage = `url('https://picsum.photos/seed/${v}/300/200.jpg')`;
                        const title = document.createElement("p"); title.textContent = v;
                        div.appendChild(thumbnail); div.appendChild(title);
                        div.onclick = () => playVideo(v);
                        videoList.appendChild(div);
                    });
                } catch (error) {
                    console.error('Error fetching videos:', error);
                    const videoList = document.getElementById("videoList");
                    videoList.innerHTML = `<p style='color: var(--danger); text-align: center; grid-column: 1/-1; padding: 20px;'><i class='fas fa-exclamation-circle'></i> Error loading videos.</p>`;
                }
            }

            async function playVideo(fileName) {
                const videoPlayer = document.getElementById("videoPlayer");
                const loadingMsg = document.getElementById("loadingMsg");
                const videoError = document.getElementById("videoError");
                const videoErrorText = document.getElementById("videoErrorText");

                videoPlayer.style.display = "none"; videoError.style.display = "none";
                loadingMsg.style.display = "block";

                const videoUrl = `${CONFIG.baseUrl}/videos/${fileName}`;

                try {
                    const response = await apiRequest(videoUrl);
                    const blob = await response.blob();
                    const blobUrl = URL.createObjectURL(blob);

                    videoPlayer.src = blobUrl;
                    videoPlayer.oncanplay = () => { videoPlayer.style.display = "block"; loadingMsg.style.display = "none"; };
                    videoPlayer.onerror = (e) => { loadingMsg.style.display = "none"; videoError.style.display = "block"; videoErrorText.textContent = "Error loading video."; };
                    videoPlayer.onstalled = () => { loadingMsg.innerHTML = '<div class="loading"></div><p style="color: var(--warning);">Stalled, retrying...</p>'; videoPlayer.load(); };
                    videoPlayer.onunload = () => URL.revokeObjectURL(blobUrl);
                    videoPlayer.load();
                } catch (err) {
                    console.error('Error loading video:', err);
                    loadingMsg.style.display = "none"; videoError.style.display = "block";
                    videoErrorText.textContent = "Error loading video: " + err.message;
                }
            }

            window.uploadVideoFromUrl = async function () {
                const urlInput = document.getElementById("videoUrlInput");
                const status = document.getElementById("urlUploadStatus");
                const progressBar = document.getElementById("uploadProgress");
                const uploadProgressBar = document.getElementById("uploadProgressBar");
                const url = urlInput.value.trim();

                if (!url) { status.innerHTML = "<i class='fas fa-exclamation-circle'></i> Please enter a URL"; return; }

                status.innerHTML = "<i class='fas fa-spinner fa-spin'></i> Downloading...";
                progressBar.style.display = "block";

                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/upload-url`, { method: "POST", body: JSON.stringify({ url }) });
                    const data = await response.json();

                    if (data.success) {
                        status.innerHTML = `<i class='fas fa-check-circle' style='color: var(--success);'></i> ${data.message}`;
                        urlInput.value = ''; progressBar.style.display = "none";
                        showToast('Upload Successful', data.message, 'success');
                        setTimeout(fetchVideos, 1000);
                    } else {
                        status.innerHTML = `<i class='fas fa-exclamation-circle' style='color: var(--danger);'></i> ${data.message}`;
                        progressBar.style.display = "none";
                        showToast('Upload Failed', data.message, 'error');
                    }
                } catch (err) {
                    console.error('Upload error:', err);
                    status.innerHTML = `<i class='fas fa-exclamation-circle' style='color: var(--danger);'></i> Error: ${err.message}`;
                    progressBar.style.display = "none";
                    showToast('Upload Failed', err.message, 'error');
                }
            };

            async function fetchPlugins() {
                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/plugins`);
                    const data = await response.json();

                    const pluginsList = document.getElementById('pluginsList');

                    if (!data.success || !data.plugins || data.plugins.length === 0) {
                        pluginsList.innerHTML = `
              <div class="empty-plugins">
                <i class="fas fa-plug"></i>
                <h3>No plugins installed</h3>
                <p>Upload a plugin file to get started</p>
              </div>
            `;
                        return;
                    }

                    pluginsList.innerHTML = '';
                    data.plugins.forEach(plugin => {
                        const pluginCard = document.createElement('div');
                        pluginCard.className = 'plugin-card';
                        pluginCard.innerHTML = `
              <div class="plugin-header">
                <div class="plugin-title">
                  <i class="fas fa-cube"></i>
                  <span>${plugin.name}</span>
                </div>
                <div class="plugin-status ${plugin.enabled ? 'active' : 'inactive'}">
                  ${plugin.enabled ? 'Active' : 'Inactive'}
                </div>
              </div>
              <div class="plugin-description">
                Plugin version ${plugin.version}
              </div>
              <div class="plugin-actions">
              </div>
              <div class="plugin-content" id="plugin-${plugin.id}-content">
                <div class="plugin-form">
                  <div class="plugin-form-group">
                    <label>Version</label>
                    <input type="text" value="${plugin.version || 'Unknown'}" readonly>
                  </div>
                  <div class="plugin-form-group">
                    <label>API Routes</label>
                    <div class="plugin-result show">
                      <p>This plugin registers API routes with the server</p>
                    </div>
                  </div>
                </div>
              </div>
            `;
                        pluginsList.appendChild(pluginCard);
                    });
                } catch (error) {
                    console.error('Error fetching plugins:', error);
                    const pluginsList = document.getElementById('pluginsList');
                    pluginsList.innerHTML = `
            <div class="plugin-result error show">
              <i class="fas fa-exclamation-circle"></i> Error loading plugins: ${error.message}
            </div>
          `;
                }
            }

            window.reloadPlugins = async function () {
                const reloadBtn = document.querySelector('button[onclick="reloadPlugins()"]');
                const originalHtml = reloadBtn.innerHTML;

                reloadBtn.disabled = true;
                reloadBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Reloading...';

                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/plugins/reload`, { method: 'POST' });
                    const result = await response.json();

                    if (response.ok && result.success) {
                        showToast('Plugins Reloaded', result.message || 'Plugins have been reloaded successfully.', 'success');
                        fetchPlugins();
                    } else {
                        showToast('Reload Failed', result.message || 'Failed to reload plugins.', 'error');
                    }
                } catch (error) {
                    console.error('Error reloading plugins:', error);
                    showToast('Reload Failed', error.message, 'error');
                } finally {
                    reloadBtn.disabled = false;
                    reloadBtn.innerHTML = originalHtml;
                }
            };

            window.togglePlugin = async function (pluginId, enable) {
                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/plugins/${pluginId}/${enable ? 'enable' : 'disable'}`, {
                        method: 'POST'
                    });

                    if (response.ok) {
                        showToast('Plugin Updated', `Plugin ${enable ? 'enabled' : 'disabled'} successfully`, 'success');
                        fetchPlugins();
                    } else {
                        const error = await response.json();
                        showToast('Plugin Update Failed', error.message || 'Unknown error', 'error');
                    }
                } catch (error) {
                    console.error('Error toggling plugin:', error);
                    showToast('Plugin Update Failed', error.message, 'error');
                }
            };

            window.configurePlugin = function (pluginId) {
                const content = document.getElementById(`plugin-${pluginId}-content`);
                content.classList.toggle('show');
            };

            const pluginFileInput = document.getElementById('pluginFileInput');
            const pluginUploadStatus = document.getElementById('pluginUploadStatus');

            pluginFileInput.addEventListener('change', async function (e) {
                const file = e.target.files[0];
                if (!file) return;

                const fileName = file.name.toLowerCase();
                if (!fileName.endsWith('.dll') && !fileName.endsWith('.zip')) {
                    pluginUploadStatus.innerHTML = `<div class="plugin-result error show"><i class="fas fa-exclamation-circle"></i> Invalid file format. Please upload a .dll or .zip file.</div>`;
                    return;
                }

                const formData = new FormData();
                formData.append('plugin', file);

                pluginUploadStatus.innerHTML = ` class="plugin-result show"><i class="fas fa-spinner fa-spin"></i> Uploading plugin...</div>`;

                try {
                    const response = await fetch(`${CONFIG.baseUrl}/api/plugins/upload`, {
                        method: 'POST',
                        headers: {
                            'Authorization': `Bearer ${AuthManager.getAuth().token}`
                        },
                        body: formData
                    });

                    const result = await response.json();

                    if (response.ok && result.success) {
                        pluginUploadStatus.innerHTML = `<div class="plugin-result success show"><i class="fas fa-check-circle"></i> Plugin uploaded successfully!</div>`;
                        showToast('Plugin Uploaded', result.message || 'Plugin has been uploaded successfully', 'success');

                        pluginFileInput.value = '';

                        fetchPlugins();
                    } else {
                        pluginUploadStatus.innerHTML = `<div class="plugin-result error show"><i class="fas fa-exclamation-circle"></i> ${result.message || 'Failed to upload plugin'}</div>`;
                        showToast('Upload Failed', result.message || 'Failed to upload plugin', 'error');
                    }
                } catch (error) {
                    console.error('Error uploading plugin:', error);
                    pluginUploadStatus.innerHTML = `<div class="plugin-result error show"><i class="fas fa-exclamation-circle"></i> Error: ${error.message}</div>`;
                    showToast('Upload Failed', error.message, 'error');
                }
            });

            async function fetchSites() {
                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/sites`);
                    const data = await response.json();

                    const sitesList = document.getElementById('sitesList');

                    if (!data || Object.keys(data).length === 0) {
                        sitesList.innerHTML = `
              <div class="empty-plugins">
                <i class="fas fa-globe"></i>
                <h3>No sites configured</h3>
                <p>Create your first site using the form above</p>
              </div>
            `;
                        return;
                    }

                    sitesList.innerHTML = '';
                    Object.entries(data).forEach(([name, port]) => {
                        const siteItem = document.createElement('div');
                        siteItem.className = 'site-item';
                        siteItem.innerHTML = `
              <div class="site-info">
                <div class="site-name">${name}</div>
                <div class="site-port">Port: ${port}</div>
              </div>
              <div class="site-actions">
                <a href="https://sftp.voidgames.ir">
                  <button class="site-action-btn sftpOpen">
                    Manage Files
                  </button>
                </a>
                <button class="site-action-btn delete" onclick="deleteSite('${name}')">
                  <i class="fas fa-trash"></i> Delete
                </button>
              </div>
            `;
                        sitesList.appendChild(siteItem);
                    });
                } catch (error) {
                    console.error('Error fetching sites:', error);
                    const sitesList = document.getElementById('sitesList');
                    sitesList.innerHTML = `
            <div class="plugin-result error show">
              <i class="fas fa-exclamation-circle"></i> Error loading sites: ${error.message}
            </div>
          `;
                }
            }

            const createSiteForm = document.getElementById('createSiteForm');
            createSiteForm.addEventListener('submit', async function (e) {
                e.preventDefault();

                const siteName = document.getElementById('siteName').value.trim();
                const statusDiv = document.getElementById('createSiteStatus');
                const submitBtn = document.getElementById('createSiteBtn');
                const originalBtnText = submitBtn.innerHTML;

                document.getElementById('siteNameError').style.display = 'none';

                let hasError = false;

                if (!siteName || siteName.length < 1) {
                    document.getElementById('siteNameError').style.display = 'block';
                    hasError = true;
                }

                if (hasError) return;

                submitBtn.disabled = true;
                submitBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Creating...';
                statusDiv.innerHTML = '';

                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/sites`, {
                        method: 'POST',
                        body: JSON.stringify({
                            name: siteName,
                            port: 11005
                        })
                    });

                    const contentType = response.headers.get('content-type');
                    if (!contentType || !contentType.includes('application/json')) {
                        throw new Error('Server returned non-JSON response');
                    }

                    const result = await response.json();

                    if (response.ok && result.success) {
                        statusDiv.innerHTML = `
            <div class="plugin-result success show">
              <i class="fas fa-check-circle"></i> ${result.message || 'Site created successfully!'}
            </div>
          `;
                        createSiteForm.reset();
                        showToast('Site Created', result.message || 'Site has been created successfully', 'success');
                        fetchSites();
                    } else {
                        statusDiv.innerHTML = `
            <div class="plugin-result error show">
              <i class="fas fa-exclamation-circle"></i> ${result.message || 'Failed to create site'}
            </div>
          `;
                        showToast('Creation Failed', result.message || 'Failed to create site', 'error');
                    }
                } catch (error) {
                    console.error('Error creating site:', error);
                    statusDiv.innerHTML = `
          <div class="plugin-result error show">
            <i class="fas fa-exclamation-circle"></i> Error: ${error.message}
          </div>
        `;
                    showToast('Creation Failed', error.message, 'error');
                } finally {
                    submitBtn.disabled = false;
                    submitBtn.innerHTML = originalBtnText;
                }
            });

            window.deleteSite = async function (siteName) {
                if (!confirm(`Are you sure you want to delete ${siteName}?`)) return;

                try {
                    const response = await apiRequest(`${CONFIG.baseUrl}/api/sites`, {
                        method: 'DELETE',
                        body: JSON.stringify({ name: siteName })
                    });

                    const contentType = response.headers.get('content-type');
                    if (!contentType || !contentType.includes('application/json')) {
                        throw new Error('Server returned non-JSON response');
                    }

                    const result = await response.json();

                    if (response.ok && result.success) {
                        showToast('Site Deleted', result.message || 'Site has been deleted successfully', 'success');
                        fetchSites();
                    } else {
                        showToast('Deletion Failed', result.message || 'Failed to delete site', 'error');
                    }
                } catch (error) {
                    console.error('Error deleting site:', error);
                    showToast('Deletion Failed', error.message, 'error');
                }
            };

            fetchStats(); fetchSystem(); fetchLogs(); fetchVideos();

            setInterval(() => { fetchStats(); fetchSystem(); fetchLogs(); }, 1000);

            setInterval(fetchVideos, 5000);
        }

// Auto-populate Swagger with JWT token for authenticated users
(function() {
    // Wait for Swagger UI to be fully loaded
    window.addEventListener('load', function() {
        // Wait a bit for Swagger UI to initialize
        setTimeout(function() {
            // Try to get JWT token from the API
            fetch('/api/Login/GetToken', {
                method: 'GET',
                credentials: 'include' // Include cookies for authentication
            })
            .then(response => {
                if (response.ok) {
                    return response.json();
                }
                throw new Error('Not authenticated');
            })
            .then(data => {
                if (data.token) {
                    // Auto-fill the authorization
                    const ui = window.ui;
                    if (ui) {
                        // For Http Bearer scheme, just pass the token (without "Bearer " prefix)
                        ui.preauthorizeApiKey('Bearer', data.token);
                        console.log('✓ Swagger auto-authenticated with JWT token');

                        // Show a notification
                        const notification = document.createElement('div');
                        notification.style.cssText = 'position: fixed; top: 10px; right: 10px; background: #49cc90; color: white; padding: 12px 20px; border-radius: 4px; z-index: 9999; font-family: sans-serif; font-size: 14px; box-shadow: 0 2px 8px rgba(0,0,0,0.2);';
                        notification.textContent = '✓ Auto-authenticated with JWT token';
                        document.body.appendChild(notification);

                        // Remove notification after 3 seconds
                        setTimeout(() => notification.remove(), 3000);
                    }
                }
            })
            .catch(error => {
                console.log('ℹ Swagger auth: User not logged in or token fetch failed');
            });
        }, 1000); // Wait 1 second for Swagger UI to initialize
    });
})();

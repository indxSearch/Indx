# OAuth Setup Guide

This guide walks you through setting up OAuth authentication with Microsoft and Google for IndxServer.

## Table of Contents
- [Microsoft OAuth (Azure)](#microsoft-oauth-azure)
- [Google OAuth](#google-oauth)
- [Configuration](#configuration)

---

## Microsoft OAuth (Azure)

### Prerequisites
- An Azure account ([Create one for free](https://azure.microsoft.com/free/))
- Access to Azure Portal

### Step 1: Create App Registration

1. Navigate to [Azure Portal](https://portal.azure.com)
2. Search for and select **App registrations**
3. Click **+ New registration**
4. Fill in the registration form:
   - **Name**: `IndxServer` (or your preferred name)
   - **Supported account types**: Choose based on your needs:
     - `Accounts in any organizational directory and personal Microsoft accounts` (most common)
   - **Redirect URI**:
     - Platform: `Web`
     - URL: `https://localhost:5001/signin-microsoft` (for development)
5. Click **Register**

### Step 2: Note Your Application (client) ID

After registration, you'll see the **Overview** page:
- Copy the **Application (client) ID** - you'll need this later
- Example: `75564f23-1948-4720-b680-4a102ac4fd3e`

### Step 3: Create Client Secret

1. In your app registration, navigate to **Certificates & secrets** (left sidebar)
2. Click **+ New client secret**
3. Add a description: `IndxServer Development`
4. Choose an expiration period (recommended: 180 days or 1 year)
5. Click **Add**
6. **IMPORTANT**: Copy the **Value** immediately - it won't be shown again
   - This is your Client Secret

### Step 4: Configure Redirect URIs

1. Navigate to **Authentication** (left sidebar)
2. Under **Platform configurations**, click on your Web platform
3. Add multiple redirect URIs for different environments:
   - Development: `https://localhost:5001/signin-microsoft`
   - Production: `https://yourdomain.com/signin-microsoft`
4. Under **Implicit grant and hybrid flows**, check:
   - ✅ ID tokens (used for implicit and hybrid flows)
5. Click **Save**

### Step 5: Configure API Permissions (Optional)

For basic authentication, the default permissions are sufficient. If you need additional scopes:
1. Navigate to **API permissions**
2. Default permissions include:
   - `User.Read` - Read user profile

---

## Google OAuth

### Prerequisites
- A Google account
- Access to Google Cloud Console

### Step 1: Create a Google Cloud Project

1. Navigate to [Google Cloud Console](https://console.cloud.google.com/)
2. Create a new project or select an existing one

### Step 2: Configure the OAuth Consent Screen

1. Navigate to **APIs & Services** > **OAuth consent screen**
2. Choose **External** as the user type
3. Fill in the required fields:
   - **App name**: `IndxServer` (or your preferred name)
   - **User support email**: your email
   - **Developer contact information**: your email
4. Click **Save and Continue** through the Scopes and Test Users screens
5. Click **Back to Dashboard**

### Step 3: Create OAuth Credentials

1. Navigate to **APIs & Services** > **Credentials**
2. Click **+ CREATE CREDENTIALS** > **OAuth client ID**
3. Fill in the form:
   - **Application type**: Web application
   - **Name**: `IndxServer`
   - **Authorized redirect URIs**:
     - `https://localhost:5001/signin-google` (development)
     - `https://yourdomain.com/signin-google` (production)
4. Click **Create**
5. Copy your **Client ID** and **Client Secret**

---

## Configuration

### Using User Secrets (Development - Recommended)

User Secrets keep sensitive data outside your project directory and git repository.

**Set Microsoft OAuth:**
```bash
dotnet user-secrets set "Authentication:Microsoft:ClientId" "your-client-id-here"
dotnet user-secrets set "Authentication:Microsoft:ClientSecret" "your-client-secret-here"
```

**Set Google OAuth:**
```bash
dotnet user-secrets set "Authentication:Google:ClientId" "your-client-id-here"
dotnet user-secrets set "Authentication:Google:ClientSecret" "your-client-secret-here"
```

**View all secrets:**
```bash
dotnet user-secrets list
```

### Using appsettings.json (Not Recommended for Production)

Only use this for testing, and never commit these values to git:

```json
{
  "Authentication": {
    "Google": {
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret"
    },
    "Microsoft": {
      "ClientId": "your-microsoft-client-id",
      "ClientSecret": "your-microsoft-client-secret"
    }
  }
}
```

### Using Environment Variables (Production)

For production deployment (Azure App Service, Docker, etc.):

**Azure App Service:**
1. Navigate to your App Service in Azure Portal
2. Go to **Configuration** > **Application settings**
3. Add new application settings:
   - `Authentication__Microsoft__ClientId`: your-client-id
   - `Authentication__Microsoft__ClientSecret`: your-client-secret
   - `Authentication__Google__ClientId`: your-client-id
   - `Authentication__Google__ClientSecret`: your-client-secret

**Docker/Environment Variables:**
```bash
export Authentication__Microsoft__ClientId="your-client-id"
export Authentication__Microsoft__ClientSecret="your-client-secret"
export Authentication__Google__ClientId="your-client-id"
export Authentication__Google__ClientSecret="your-client-secret"
```

---

## Testing Your Configuration

1. Run your application:
   ```bash
   dotnet run
   ```

2. Navigate to the login page: `https://localhost:5001/Account/Login`

3. You should see buttons for:
   - Sign in with Microsoft
   - Sign in with Google

4. Click a button and verify you're redirected to the OAuth provider

5. After signing in, you should be redirected back to your application

---

## Troubleshooting

### "unauthorized_client" Error

**Problem**: The OAuth provider rejects the authentication attempt.

**Solutions**:
- Verify your redirect URI in the provider's configuration matches exactly (including http/https, port, and path)
- Check that your Client ID and Client Secret are correct
- Ensure the app registration is properly configured

### "AADSTS50011: The redirect URI specified in the request does not match"

**Problem**: Microsoft redirect URI mismatch.

**Solution**:
- Add the exact redirect URI to your Azure App Registration under **Authentication** > **Redirect URIs**
- Common URI to add:
  - `https://localhost:5001/signin-microsoft`

### Provider Button Doesn't Appear

**Problem**: OAuth provider buttons are not visible on login page.

**Solution**:
- Check that secrets are properly configured (`dotnet user-secrets list`)
- Verify the application is reading the configuration (check console output on startup)
- Make sure Client ID doesn't start with placeholder values like "your-"

---

## Security Best Practices

1. **Never commit secrets to git**
   - Use User Secrets for development
   - Use Azure Key Vault or environment variables for production

2. **Rotate secrets regularly**
   - Set expiration dates on client secrets
   - Update secrets before they expire

3. **Use HTTPS in production**
   - OAuth providers require HTTPS redirect URIs in production
   - Configure SSL certificates properly

4. **Limit redirect URIs**
   - Only add redirect URIs you actually use
   - Remove development URIs from production app registrations

5. **Monitor sign-ins**
   - Review sign-in logs in Azure Portal
   - Monitor for suspicious activity

---

## Additional Resources

- [Microsoft Identity Platform Documentation](https://docs.microsoft.com/azure/active-directory/develop/)
- [Google OAuth 2.0 Documentation](https://developers.google.com/identity/protocols/oauth2)
- [ASP.NET Core External Authentication](https://docs.microsoft.com/aspnet/core/security/authentication/social/)

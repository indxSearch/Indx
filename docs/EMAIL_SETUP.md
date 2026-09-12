# Email Service Setup Guide

This guide walks you through setting up email functionality for IndxServer. The application supports three email providers:
- **Console** (default) - Logs emails to console for development
- **Azure Communication Services** (recommended) - Production-ready email service
- **Custom** - Implement your own `IEmailSender`

## Table of Contents
- [Console Email (Default)](#console-email-default)
- [Azure Communication Services](#azure-communication-services)
- [Custom Email Provider](#custom-email-provider)
- [Configuration](#configuration)
- [Testing](#testing)

---

## Console Email (Default)

The Console email provider is enabled by default and requires no configuration. Emails are logged to the console output instead of being sent.

**Use cases:**
- Local development
- Testing email flows without sending real emails
- CI/CD pipelines

**Configuration:**
```json
{
  "Email": {
    "Provider": "Console"
  }
}
```

**Output example:**
```
=== EMAIL (Console Mode) ===
To: user@example.com
Subject: Reset Your Password
Body: <html>...</html>
```

---

## Azure Communication Services

Azure Communication Services is a cloud-based communication platform that provides reliable email delivery with built-in domain management.

### Prerequisites
- An Azure account ([Create one for free](https://azure.microsoft.com/free/))
- Access to Azure Portal

### Step 1: Create Azure Communication Services Resource

1. Navigate to [Azure Portal](https://portal.azure.com)
2. Click **+ Create a resource**
3. Search for **Communication Services**
4. Click **Create**
5. Fill in the form:
   - **Subscription**: Your Azure subscription
   - **Resource group**: Create new or use existing
   - **Resource name**: `indx-communication-services` (or your preferred name)
   - **Region**: Choose closest to your users
   - **Data location**: Choose your preferred data residency
6. Click **Review + create** > **Create**
7. Wait for deployment to complete

### Step 2: Create Email Communication Services Resource

1. In Azure Portal, click **+ Create a resource**
2. Search for **Email Communication Services**
3. Click **Create**
4. Fill in the form:
   - **Subscription**: Your Azure subscription
   - **Resource group**: Same as Communication Services resource
   - **Email service name**: `indx-email-services` (or your preferred name)
   - **Region**: Choose same region as Communication Services
   - **Data location**: Choose same as Communication Services
5. Click **Review + create** > **Create**
6. Wait for deployment to complete

### Step 3: Provision Email Domain

You have two options: **Azure Managed Domain** (easiest) or **Custom Domain**

#### Option A: Azure Managed Domain (Recommended for Quick Setup)

1. Navigate to your **Email Communication Services** resource
2. Click **Provision Domains** in the left sidebar
3. Click **+ Add domain**
4. Select **Azure Managed Domain**
5. Azure will provision a domain like: `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.azurecomm.net`
6. Wait for provisioning to complete (1-2 minutes)
7. Copy the domain name - you'll need this for the sender address

#### Option B: Custom Domain (For Production)

1. Navigate to your **Email Communication Services** resource
2. Click **Provision Domains** in the left sidebar
3. Click **+ Add domain**
4. Select **Custom Domain**
5. Enter your domain: `yourdomain.com`
6. Follow the DNS verification steps:
   - Add the provided TXT records to your DNS
   - Add the provided SPF, DKIM, and DMARC records
7. Click **Verify** once DNS records are propagated
8. Wait for verification to complete (can take up to 24 hours)

### Step 4: Link Resources Together

1. Navigate to your **Communication Services** resource (not Email Services)
2. Click **Domains** in the left sidebar under **Email**
3. Click **+ Connect domain**
4. Select your provisioned domain from the dropdown
5. Click **Connect**
6. Wait for connection to complete

### Step 5: Get Connection String

1. In your **Communication Services** resource
2. Click **Keys** in the left sidebar
3. Copy either the **Primary connection string** or **Secondary connection string**
4. It will look like:
   ```
   endpoint=https://indx-communication-services.europe.communication.azure.com/;accesskey=your-access-key-here
   ```

### Step 6: Configure Sender Address

⚠️ **CRITICAL**: The `FromAddress` in your configuration **MUST** match a domain that is verified and connected in your Azure Communication Services resource. Azure will reject emails from unverified domains.

Your sender address format depends on the domain type:

**Azure Managed Domain:**
```
DoNotReply@xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx.azurecomm.net
```
Replace `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx` with your actual Azure-provisioned domain ID.

**Custom Domain:**
```
noreply@yourdomain.com
```
Only works if you've verified and connected `yourdomain.com` in Azure (see Option B above).

---

## Custom Email Provider

You can implement your own email provider by creating a class that implements `IEmailSender`:

### Step 1: Create Your Email Service

Create a new file in `Services/YourEmailSender.cs`:

```csharp
using Microsoft.AspNetCore.Identity.UI.Services;

namespace IndxServer.Services;

public class YourEmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<YourEmailSender> _logger;

    public YourEmailSender(IConfiguration configuration, ILogger<YourEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        // Your email sending logic here
        // Examples: SMTP, SendGrid, Mailgun, AWS SES, etc.

        _logger.LogInformation("Email sent to {Email} with subject {Subject}", email, subject);
    }
}
```

### Step 2: Register Your Service

Update `Program.cs` to add your custom provider:

```csharp
var emailProvider = builder.Configuration["Email:Provider"]?.ToLower() ?? "console";

switch (emailProvider)
{
    case "azurecommunicationservices":
    case "acs":
        // ... existing code
        break;

    case "your-provider":
        builder.Services.AddTransient<IEmailSender, YourEmailSender>();
        Console.WriteLine("✓ Email configured: Your Custom Provider");
        break;

    case "console":
    default:
        builder.Services.AddTransient<IEmailSender, ConsoleEmailSender>();
        Console.WriteLine("ℹ Email configured: Console mode");
        break;
}
```

### Step 3: Add Configuration

Update `appsettings.json`:

```json
{
  "Email": {
    "Provider": "your-provider",
    "YourProvider": {
      "ApiKey": "",
      "SomeOtherSetting": ""
    }
  }
}
```

---

## Configuration

### Using User Secrets (Development - Recommended)

**Azure Communication Services:**
```bash
dotnet user-secrets set "Email:Provider" "AzureCommunicationServices"
dotnet user-secrets set "Email:AzureCommunicationServices:ConnectionString" "endpoint=https://...;accesskey=..."
dotnet user-secrets set "Email:FromAddress" "DoNotReply@xxxxxxxx.azurecomm.net"
```

**Console mode:**
```bash
dotnet user-secrets set "Email:Provider" "Console"
```

**View current configuration:**
```bash
dotnet user-secrets list
```

### Using appsettings.json

Update `appsettings.json` (not recommended for secrets):

```json
{
  "Email": {
    "Provider": "AzureCommunicationServices",
    "FromAddress": "noreply@yourdomain.com",
    "FromName": "Indx Authentication",
    "AzureCommunicationServices": {
      "ConnectionString": "endpoint=https://...;accesskey=..."
    }
  }
}
```

### Using Environment Variables (Production)

**Azure App Service:**
1. Navigate to your App Service in Azure Portal
2. Go to **Configuration** > **Application settings**
3. Add new application settings:
   - `Email__Provider`: `AzureCommunicationServices`
   - `Email__AzureCommunicationServices__ConnectionString`: your-connection-string
   - `Email__FromAddress`: your-sender-address

**Docker:**
```bash
docker run -e Email__Provider="AzureCommunicationServices" \
           -e Email__AzureCommunicationServices__ConnectionString="endpoint=..." \
           -e Email__FromAddress="noreply@yourdomain.com" \
           your-image
```

---

## Testing

### Test Password Reset Flow

1. Run your application:
   ```bash
   dotnet run
   ```

2. Navigate to: `https://localhost:5001/Account/Login`

3. Click **Forgot your password?**

4. Enter an email address of a registered user

5. **Console mode**: Check console output for the reset link
   ```
   === EMAIL (Console Mode) ===
   To: user@example.com
   Subject: Reset Your Password
   Body: <html>...reset link here...</html>
   ```

6. **Azure Communication Services**: Check the recipient's inbox

7. Click the reset link and verify you can set a new password

### Test Registration Confirmation

1. Navigate to: `https://localhost:5001/Account/Register`

2. Register a new account

3. Check console output or email inbox for confirmation email

---

## Troubleshooting

### Emails Not Sending (Azure Communication Services)

**Check 1: Verify Connection String**
```bash
dotnet user-secrets list | grep Email
```
Ensure the connection string is correct and complete.

**Check 2: Verify Domain Connection**
- Navigate to Communication Services resource in Azure Portal
- Click **Domains** > Verify domain is connected

**Check 3: Check Sender Address**
- Ensure sender address matches your provisioned domain
- Format: `noreply@your-provisioned-domain.azurecomm.net`

**Check 4: Review Logs**
- Check Azure Portal > Communication Services > Logs
- Look for email delivery status and errors

### "Email Failed to Send" Error

**Problem**: Connection string is invalid or expired.

**Solution**:
1. Navigate to Communication Services in Azure Portal
2. Go to **Keys** and regenerate if needed
3. Update your user secrets with new connection string

### Emails Going to Spam

**Problem**: Emails are being filtered as spam.

**Solutions**:
1. **Use custom domain** with proper SPF, DKIM, and DMARC records
2. **Verify sender reputation** in Azure Portal
3. **Warm up your domain** by gradually increasing send volume
4. **Avoid spam trigger words** in email content

### Console Mode Not Working

**Problem**: No email output in console.

**Solution**:
- Verify `Email:Provider` is set to `Console`
- Check logging level in `appsettings.json`:
  ```json
  {
    "Logging": {
      "LogLevel": {
        "Default": "Information"
      }
    }
  }
  ```

---

## Email Templates

The application sends HTML emails for:
- **Password Reset**: Reset link valid for 1 hour
- **Email Confirmation**: Confirm account email address
- **Account Changes**: Notifications about account modifications

Templates are defined inline in the Razor components. To customize:

1. Navigate to the relevant component:
   - `Components/Account/Pages/ForgotPassword.razor` - Password reset email
   - `Components/Account/Pages/Register.razor` - Confirmation email

2. Modify the HTML email template in the `EmailSender.SendEmailAsync()` call

---

## Cost Considerations

### Azure Communication Services Pricing

**Email:**
- First 1,000 emails/month: Free
- Additional emails: $0.001 per email (as of 2025)

**Connection:**
- No charge for maintaining the resource

**Custom domain:**
- $0.20 per domain per month

**Azure Managed Domain:**
- Free

### Recommendations

**Development:**
- Use Console mode (free)

**Small projects:**
- Use Azure managed domain with free tier (up to 1,000 emails/month)

**Production:**
- Use custom domain for better deliverability
- Monitor usage in Azure Portal

---

## Security Best Practices

1. **Never commit connection strings to git**
   - Use User Secrets for development
   - Use Azure Key Vault or environment variables for production

2. **Rotate access keys regularly**
   - Use Azure Portal to regenerate keys
   - Update configuration immediately after rotation

3. **Implement rate limiting**
   - Limit password reset requests per user
   - Prevent email enumeration attacks

4. **Use HTTPS**
   - All email links should use HTTPS
   - Especially important for password reset links

5. **Set appropriate token expiration**
   - Password reset: 1 hour (default)
   - Email confirmation: 24 hours recommended

---

## Additional Resources

- [Azure Communication Services Documentation](https://docs.microsoft.com/azure/communication-services/)
- [Email Service Documentation](https://docs.microsoft.com/azure/communication-services/concepts/email/email-overview)
- [ASP.NET Core Identity Email Confirmation](https://docs.microsoft.com/aspnet/core/security/authentication/accconfirm)
- [Azure Communication Services Pricing](https://azure.microsoft.com/pricing/details/communication-services/)

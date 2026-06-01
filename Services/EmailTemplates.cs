namespace IndxCloudApi.Services;

public static class EmailTemplates
{
    public static string Invite(string instanceName, string registerUrl) =>
        Build(
            instanceName,
            "You have been invited",
            $"You have been invited to join <strong>{instanceName}</strong>. Click the button below to create your account.",
            registerUrl,
            "Create Account",
            "If you were not expecting this invitation, you can safely ignore this email."
        );

    public static string Subject(string instanceName, string message) =>
        $"Indx Search System - {instanceName} - {message}";

    public static string VerifyEmail(string instanceName, string confirmUrl) =>
        Build(
            instanceName,
            "Verify your email address",
            $"You're almost set up on <strong>{instanceName}</strong>. Click the button below to verify your email address and activate your account.",
            confirmUrl,
            "Verify Email Address",
            "If you didn't create an account, you can safely ignore this email."
        );

    public static string ResetPassword(string instanceName, string resetUrl) =>
        Build(
            instanceName,
            "Reset your password",
            $"We received a request to reset the password for your <strong>{instanceName}</strong> account. Click the button below to choose a new password. This link expires in 1 hour.",
            resetUrl,
            "Reset Password",
            "If you didn't request a password reset, you can safely ignore this email. Your password will not change."
        );

    public static string ConfirmEmailChange(string instanceName, string confirmUrl) =>
        Build(
            instanceName,
            "Confirm your new email address",
            $"You requested an email address change on your <strong>{instanceName}</strong> account. Click the button below to confirm this is correct.",
            confirmUrl,
            "Confirm New Email",
            "If you didn't request this change, please sign in and review your account security."
        );

    public static string ResendConfirmation(string instanceName, string confirmUrl) =>
        Build(
            instanceName,
            "Verify your email address",
            $"Here is a new verification link for your <strong>{instanceName}</strong> account. Click the button below to verify your email address.",
            confirmUrl,
            "Verify Email Address",
            "If you didn't create an account, you can safely ignore this email."
        );

    private static string Build(
        string instanceName,
        string heading,
        string body,
        string actionUrl,
        string actionLabel,
        string disclaimer)
    {
        return $@"<!DOCTYPE html>
<html>
<head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""></head>
<body style=""margin:0;padding:0;background:#FFFFFF;font-family:'Inter',-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;"">
  <table width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""padding:48px 24px;"">
    <tr>
      <td align=""center"">
        <table width=""520"" cellpadding=""0"" cellspacing=""0"" style=""max-width:520px;width:100%;"">

          <!-- Wordmark -->
          <tr>
            <td style=""padding:0 0 16px;"">
              <span style=""font-size:13px;font-weight:350;color:#4A4A50;"">Indx Search System</span>
              <span style=""font-size:13px;font-weight:350;color:#CFCFCF;""> &middot; {instanceName}</span>
            </td>
          </tr>

          <!-- Card: lv1 background with lv3 border -->
          <tr>
            <td style=""background:#FBFBFB;border:1px solid #CFCFCF;"">
              <table width=""100%"" cellpadding=""0"" cellspacing=""0"">
                <tr>
                  <td style=""padding:32px 36px 28px;"">

                    <!-- Instance label -->
                    <p style=""margin:0 0 12px;font-size:11px;font-weight:350;color:#757575;"">{instanceName}</p>

                    <!-- Heading: 19px, weight 350 — matches h2 in app -->
                    <h2 style=""margin:0 0 16px;font-size:19px;font-weight:350;color:#1A1A21;line-height:1.3;"">{heading}</h2>

                    <!-- Body: 13px, weight 350, line-height 1.8 — matches base text in app -->
                    <p style=""margin:0 0 28px;font-size:13px;font-weight:350;line-height:1.8;color:#4A4A50;"">{body}</p>

                    <!-- Primary button: matches .btn-primary exactly -->
                    <table cellpadding=""0"" cellspacing=""0"" style=""margin-bottom:28px;"">
                      <tr>
                        <td style=""background:#121215;border-radius:1px;"">
                          <a href=""{actionUrl}""
                             style=""display:inline-block;padding:0 15px;height:30px;line-height:30px;color:#FBFBFB;text-decoration:none;font-size:13px;font-weight:350;white-space:nowrap;"">{actionLabel}</a>
                        </td>
                      </tr>
                    </table>

                    <!-- Fallback link -->
                    <p style=""margin:0;font-size:11px;font-weight:350;line-height:1.8;color:#757575;"">
                      Or copy this link into your browser:<br>
                      <a href=""{actionUrl}"" style=""color:#4A4A50;word-break:break-all;"">{actionUrl}</a>
                    </p>

                  </td>
                </tr>

                <!-- Footer inside card -->
                <tr>
                  <td style=""padding:16px 36px;border-top:1px solid #EFEFEF;"">
                    <p style=""margin:0;font-size:11px;font-weight:350;color:#CFCFCF;line-height:1.6;"">{disclaimer}</p>
                    <p style=""margin:4px 0 0;font-size:11px;font-weight:350;color:#CFCFCF;"">Sent by {instanceName} &middot; Indx Search System</p>
                  </td>
                </tr>

              </table>
            </td>
          </tr>

        </table>
      </td>
    </tr>
  </table>
</body>
</html>";
    }
}

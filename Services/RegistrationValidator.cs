namespace IndxCloudApi.Services;

internal class RegistrationValidator
{
    private readonly InstanceSettingsService _settingsService;
    private readonly ILogger<RegistrationValidator> _logger;

    public RegistrationValidator(
        InstanceSettingsService settingsService,
        ILogger<RegistrationValidator> logger)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    public RegistrationValidationResult ValidateRegistrationAllowed()
    {
        var settings = _settingsService.Load();

        if (settings.RegistrationMode == RegistrationMode.Closed)
        {
            _logger.LogWarning("Registration attempt blocked - registration is closed");
            return RegistrationValidationResult.Failure(
                "Registration is currently closed. Please contact the administrator for access.");
        }

        return RegistrationValidationResult.Success();
    }

    public RegistrationValidationResult ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return RegistrationValidationResult.Failure("Email is required.");

        var settings = _settingsService.Load();

        if (settings.RegistrationMode == RegistrationMode.Open)
            return RegistrationValidationResult.Success();

        if (settings.RegistrationMode == RegistrationMode.Closed)
            return RegistrationValidationResult.Failure(
                "Registration is currently closed. Please contact the administrator for access.");

        if (settings.RegistrationMode == RegistrationMode.EmailDomain)
        {
            if (settings.AllowedDomains.Count == 0)
            {
                _logger.LogWarning("EmailDomain mode configured but no allowed domains specified. Treating as Open.");
                return RegistrationValidationResult.Success();
            }

            var domain = GetEmailDomain(email);
            if (string.IsNullOrEmpty(domain))
                return RegistrationValidationResult.Failure("Invalid email format.");

            if (!settings.AllowedDomains.Any(d => domain.Equals(d, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Registration blocked for {Email} - domain not in allowed list", email);
                return RegistrationValidationResult.Failure(
                    $"Registration is restricted to: {string.Join(", ", settings.AllowedDomains)}");
            }
        }

        if (settings.RegistrationMode == RegistrationMode.Invite)
        {
            if (!settings.AllowedEmails.Any(e => e.Equals(email, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Registration blocked for {Email} - not on invite list", email);
                return RegistrationValidationResult.Failure(
                    "You have not been invited to register. Please contact the administrator.");
            }
        }

        return RegistrationValidationResult.Success();
    }

    public RegistrationMode GetCurrentMode() => _settingsService.Load().RegistrationMode;

    public string GetRegistrationInfoMessage()
    {
        var settings = _settingsService.Load();
        return settings.RegistrationMode switch
        {
            RegistrationMode.Closed => "Registration is currently closed. Please contact the administrator for access.",
            RegistrationMode.EmailDomain when settings.AllowedDomains.Count > 0 =>
                $"Registration is restricted to: {string.Join(", ", settings.AllowedDomains)}",
            RegistrationMode.Invite => $"Registration by invite only. {settings.AllowedEmails.Count} invite(s) pending.",
            _ => "Open registration — anyone can create an account."
        };
    }

    private static string? GetEmailDomain(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 || at == email.Length - 1 ? null : email[(at + 1)..];
    }
}

internal class RegistrationValidationResult
{
    public bool IsValid { get; init; }
    public string? ErrorMessage { get; init; }

    public static RegistrationValidationResult Success() => new() { IsValid = true };
    public static RegistrationValidationResult Failure(string errorMessage) =>
        new() { IsValid = false, ErrorMessage = errorMessage };
}

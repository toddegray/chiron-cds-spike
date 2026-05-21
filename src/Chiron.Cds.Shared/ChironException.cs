namespace Chiron.Cds.Shared;

/// <summary>Base for Chiron-specific error types so middleware can translate them to HTTP responses.</summary>
public abstract class ChironException : Exception
{
    protected ChironException(string message) : base(message) { }
    protected ChironException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Raised when a request references an unknown tenant id.</summary>
public sealed class UnknownTenantException : ChironException
{
    public UnknownTenantException(string tenantId)
        : base($"Unknown tenant '{tenantId}'.") { }
}

/// <summary>Raised when a SMART launch's <c>iss</c> doesn't match any configured tenant.</summary>
public sealed class UntrustedIssuerException : ChironException
{
    public UntrustedIssuerException(string iss)
        : base($"Issuer '{iss}' is not a configured tenant.") { }
}

/// <summary>Raised when an OAuth state token is missing, tampered with, or expired.</summary>
public sealed class InvalidLaunchStateException : ChironException
{
    public InvalidLaunchStateException(string message) : base(message) { }
}

/// <summary>Raised when a token endpoint returns an error or an unparseable body.</summary>
public sealed class TokenExchangeException : ChironException
{
    public TokenExchangeException(string message) : base(message) { }
    public TokenExchangeException(string message, Exception inner) : base(message, inner) { }
}

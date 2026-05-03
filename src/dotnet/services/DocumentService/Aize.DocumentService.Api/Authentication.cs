using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Aize.DocumentService.Api;

using WebUtilitiesBase64UrlTextEncoder = Microsoft.AspNetCore.WebUtilities.Base64UrlTextEncoder;

public sealed class LocalAuthenticationOptions
{
    public const string SectionName = "Authentication";

    public string Issuer { get; set; } = "Aize.DocumentService";

    public string Audience { get; set; } = "Aize.DocumentPortal";

    public int AccessTokenMinutes { get; set; } = 120;

    public List<ConfiguredUser> Users { get; set; } = [];
}

public sealed class ConfiguredUser
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string[] Roles { get; set; } = [];
}

public sealed record LoginApiRequest(string Username, string Password);

public sealed record LoginApiResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc,
    string UserId,
    string Username,
    string DisplayName,
    IReadOnlyCollection<string> Roles);

public sealed record CurrentUserApiResponse(
    string UserId,
    string Username,
    string DisplayName,
    IReadOnlyCollection<string> Roles);

internal sealed record AccessTokenPayload(
    string Subject,
    string Username,
    string DisplayName,
    string Issuer,
    string Audience,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyCollection<string> Roles);

public sealed class LocalBearerTokenService
{
    public const string AuthenticationScheme = "Bearer";
    private const string ProtectorPurpose = "auth.local-bearer-token.v1";

    private readonly IDataProtector _protector;
    private readonly LocalAuthenticationOptions _options;
    private readonly TimeProvider _timeProvider;

    public LocalBearerTokenService(
        IDataProtectionProvider dataProtectionProvider,
        IOptions<LocalAuthenticationOptions> options,
        TimeProvider timeProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public LoginApiResponse? Login(LoginApiRequest request)
    {
        var user = _options.Users.SingleOrDefault(candidate =>
            candidate.Username.Equals(request.Username, StringComparison.OrdinalIgnoreCase) &&
            candidate.Password == request.Password);

        if (user is null)
        {
            return null;
        }

        var expiresAtUtc = _timeProvider.GetUtcNow().AddMinutes(_options.AccessTokenMinutes);
        var payload = new AccessTokenPayload(
            user.UserId,
            user.Username,
            user.DisplayName,
            _options.Issuer,
            _options.Audience,
            expiresAtUtc,
            user.Roles);

        var json = JsonSerializer.Serialize(payload);
        var protectedJson = _protector.Protect(json);
        var accessToken = WebUtilitiesBase64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(protectedJson));

        return new LoginApiResponse(
            accessToken,
            "Bearer",
            expiresAtUtc,
            user.UserId,
            user.Username,
            user.DisplayName,
            user.Roles);
    }

    public ClaimsPrincipal? ValidateToken(string accessToken)
    {
        try
        {
            var protectedBytes = WebUtilitiesBase64UrlTextEncoder.Decode(accessToken);
            var protectedJson = Encoding.UTF8.GetString(protectedBytes);
            var json = _protector.Unprotect(protectedJson);
            var payload = JsonSerializer.Deserialize<AccessTokenPayload>(json);

            if (payload is null ||
                payload.ExpiresAtUtc <= _timeProvider.GetUtcNow() ||
                !string.Equals(payload.Issuer, _options.Issuer, StringComparison.Ordinal) ||
                !string.Equals(payload.Audience, _options.Audience, StringComparison.Ordinal))
            {
                return null;
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, payload.Subject),
                new(ClaimTypes.Name, payload.DisplayName),
                new("preferred_username", payload.Username)
            };

            claims.AddRange(payload.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

            var identity = new ClaimsIdentity(claims, LocalBearerTokenService.AuthenticationScheme);
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }
}

public sealed class LocalBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly LocalBearerTokenService _tokenService;

    public LocalBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        LocalBearerTokenService tokenService)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ResolveAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var principal = _tokenService.ValidateToken(token);
        if (principal is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired bearer token."));
        }

        var ticket = new AuthenticationTicket(principal, LocalBearerTokenService.AuthenticationScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private string? ResolveAccessToken()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader["Bearer ".Length..].Trim();
        }

        if (Request.Path.StartsWithSegments("/hubs/documents"))
        {
            return Request.Query["access_token"].ToString();
        }

        return null;
    }
}

public static class OpenApiSecurityTransformers
{
    public static OpenApiOperation AddBearerSecurity(OpenApiOperation operation)
    {
        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Id = "Bearer",
                        Type = ReferenceType.SecurityScheme
                    }
                }
            ] = []
        });

        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Unauthorized" });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Forbidden" });
        return operation;
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string? GetUserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? GetUsername(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("preferred_username");

    public static string? GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name);
}

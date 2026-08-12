using Microsoft.Extensions.Logging;
using TenServer.Data.Entities;
using TenServer.Data.Repositories;

namespace TenServer.Server.Web;

public enum RegistrationFailure
{
    None,
    Invalid,
    Conflict,
}

public sealed record RegistrationOutcome(
    RegistrationFailure Failure,
    string? Error,
    Account? Account)
{
    public bool Success => Failure == RegistrationFailure.None;

    public static RegistrationOutcome Invalid(string error)
        => new(RegistrationFailure.Invalid, error, null);

    public static RegistrationOutcome Conflict(string error)
        => new(RegistrationFailure.Conflict, error, null);

    public static RegistrationOutcome Created(Account account)
        => new(RegistrationFailure.None, null, account);
}

/// <summary>
/// The single path an account is created through. The browser form and the JSON API differ
/// only in how they obtain the digest — everything after that is here, so the two cannot
/// drift apart in validation, error wording or logging.
/// </summary>
public sealed class RegistrationService(IAccountRepository accounts, ILogger<RegistrationService> log)
{
    public async Task<RegistrationOutcome> RegisterAsync(
        RegisterAccountRequest request, CancellationToken ct = default)
    {
        if (RegistrationEndpoint.Validate(request) is { } problem)
        {
            log.LogWarning("Registration rejected: {Problem}", problem);
            return RegistrationOutcome.Invalid(problem);
        }

        var gameId = request.GameId!.Trim();

        var (result, account) = await accounts.RegisterAsync(
            gameId, request.PasswordHash!.Trim(), request.RegCode!.Trim(), ct);

        if (result != RegistrationResult.Created)
        {
            var reason = RegistrationEndpoint.Describe(result);
            log.LogWarning("Registration for {GameId} rejected: {Reason}", gameId, reason);
            return RegistrationOutcome.Conflict(reason);
        }

        log.LogInformation("Account {Id} registered for gameId {GameId}", account!.Id, gameId);
        return RegistrationOutcome.Created(account);
    }
}

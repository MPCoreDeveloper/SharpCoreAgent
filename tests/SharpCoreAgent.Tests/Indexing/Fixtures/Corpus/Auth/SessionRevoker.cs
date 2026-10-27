namespace Demo.Auth;

/// Revokes every refresh token of a session and records why, so the next exchange fails.
public sealed class SessionRevoker(ISessionStore sessions)
{
    public async Task RevokeAsync(string sessionId, string reason, CancellationToken cancellationToken)
    {
        var session = await sessions.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.RefreshTokens.Clear();
        session.RevokedReason = reason;
        await sessions.SaveAsync(session, cancellationToken);
    }
}

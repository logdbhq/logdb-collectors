using System.Net;

namespace com.logdb.docker.collector.ui.Security;

/// <summary>
/// Renders a self-contained (no external assets) login form so it works even before
/// the user is authenticated. Posts username/password to the <c>/login</c> endpoint.
/// </summary>
public static class LoginPage
{
    public static string Render(string? returnUrl, bool error)
    {
        var encodedReturn = WebUtility.HtmlEncode(returnUrl ?? string.Empty);
        var errorBanner = error
            ? "<p class=\"error\">Invalid username or password.</p>"
            : string.Empty;

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>Sign in · LogDB Docker Collector</title>
            <style>
                :root { color-scheme: light dark; }
                * { box-sizing: border-box; }
                body {
                    margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                    font-family: system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
                    background: #0f172a; color: #e2e8f0;
                }
                .card {
                    width: 100%; max-width: 360px; padding: 2rem;
                    background: #1e293b; border: 1px solid #334155; border-radius: 12px;
                    box-shadow: 0 10px 30px rgba(0,0,0,.4);
                }
                h1 { font-size: 1.15rem; margin: 0 0 .25rem; }
                .sub { margin: 0 0 1.5rem; font-size: .85rem; color: #94a3b8; }
                label { display: block; font-size: .8rem; margin: .75rem 0 .25rem; color: #cbd5e1; }
                input {
                    width: 100%; padding: .6rem .7rem; border-radius: 8px;
                    border: 1px solid #475569; background: #0f172a; color: #e2e8f0; font-size: .95rem;
                }
                input:focus { outline: 2px solid #3b82f6; border-color: #3b82f6; }
                button {
                    width: 100%; margin-top: 1.25rem; padding: .65rem; border: 0; border-radius: 8px;
                    background: #3b82f6; color: #fff; font-size: .95rem; font-weight: 600; cursor: pointer;
                }
                button:hover { background: #2563eb; }
                .error { margin: 0 0 1rem; padding: .6rem .7rem; border-radius: 8px;
                    background: #7f1d1d; color: #fecaca; font-size: .85rem; }
            </style>
        </head>
        <body>
            <div class="card">
                <h1>LogDB Docker Collector</h1>
                <p class="sub">Operator console — sign in to continue</p>
                {{errorBanner}}
                <form method="post" action="login">
                    <input type="hidden" name="returnUrl" value="{{encodedReturn}}" />
                    <label for="username">Username</label>
                    <input id="username" name="username" autocomplete="username" autofocus />
                    <label for="password">Password</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" />
                    <button type="submit">Sign in</button>
                </form>
            </div>
        </body>
        </html>
        """;
    }
}

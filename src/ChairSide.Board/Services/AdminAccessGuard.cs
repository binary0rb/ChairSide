using Microsoft.AspNetCore.Http;

namespace ChairSide.Board.Services;

public static class AdminAccessGuard
{
    public static bool IsProtectedPath(PathString path) =>
        path.Equals("/reports.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/reports", StringComparison.OrdinalIgnoreCase);

    public static IResult? ValidateRequest(
        HttpRequest request,
        AdminAccessTokenValidator adminAccessTokenValidator)
    {
        var token = request.Headers[AdminAccessTokenValidator.HeaderName].FirstOrDefault();

        return adminAccessTokenValidator.Validate(token) switch
        {
            AdminAccessTokenValidationResult.Disabled or AdminAccessTokenValidationResult.Valid => null,
            AdminAccessTokenValidationResult.Missing => Results.Unauthorized(),
            _ => Results.StatusCode(StatusCodes.Status403Forbidden)
        };
    }

    public static async Task WriteReportsAccessPromptAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(ReportsAccessPromptHtml);
    }

    private const string ReportsAccessPromptHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>ChairSide Board - Reports Access</title>
          <link rel="stylesheet" href="/styles.css">
        </head>
        <body>
          <main class="access-shell">
            <section class="access-card" aria-labelledby="accessTitle">
              <span class="brand-kicker">ChairSide Board</span>
              <h1 id="accessTitle">Reports Access</h1>
              <p>Enter the internal reports token for this browser session.</p>
              <form id="adminAccessForm" class="access-form">
                <label for="adminAccessToken">Reports token</label>
                <input id="adminAccessToken" name="adminAccessToken" type="password" autocomplete="off" required>
                <button type="submit" class="primary-button">Open Reports</button>
              </form>
              <p id="adminAccessMessage" class="access-message" role="status" aria-live="polite"></p>
            </section>
          </main>
          <script>
            (() => {
              const storageKey = "chairside-admin-token";
              const headerName = "X-ChairSide-Admin-Token";
              const form = document.getElementById("adminAccessForm");
              const input = document.getElementById("adminAccessToken");
              const message = document.getElementById("adminAccessMessage");

              async function openReports(token) {
                message.textContent = "Checking access...";
                const response = await fetch("/reports.html", {
                  cache: "no-store",
                  headers: { [headerName]: token }
                });

                if (response.ok) {
                  sessionStorage.setItem(storageKey, token);
                  document.open();
                  document.write(await response.text());
                  document.close();
                  return;
                }

                sessionStorage.removeItem(storageKey);
                message.textContent = response.status === 403
                  ? "Access denied. Check the token and try again."
                  : "Reports access is required.";
              }

              form.addEventListener("submit", event => {
                event.preventDefault();
                const token = input.value.trim();
                if (token) {
                  openReports(token);
                }
              });

              const savedToken = sessionStorage.getItem(storageKey);
              if (savedToken) {
                openReports(savedToken);
              }
            })();
          </script>
        </body>
        </html>
        """;
}

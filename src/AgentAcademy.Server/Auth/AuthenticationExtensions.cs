using AgentAcademy.Server.Services;
using AgentAcademy.Server.Services.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;

namespace AgentAcademy.Server.Auth;

/// <summary>
/// DI registration extensions for authentication and authorization.
/// Extracted from Program.cs to reduce churn — auth config is complex but stable.
/// </summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Registers GitHub OAuth, Consultant key auth, and authorization policies
    /// based on the precomputed <see cref="AppAuthSetup"/>.
    /// </summary>
    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        AppAuthSetup setup)
    {
        if (!setup.AnyAuthEnabled)
            return services;

        var authBuilder = services.AddAuthentication(options =>
        {
            if (setup.GitHubAuthEnabled && setup.ConsultantAuthEnabled)
            {
                options.DefaultScheme = "MultiAuth";
                // Challenge via the cookie scheme so unauthenticated API/SignalR
                // requests get a 401 (via OnRedirectToLogin) instead of a 302 to
                // GitHub's OAuth authorize URL. The SPA initiates login explicitly
                // by navigating to /api/auth/login, which Challenges "GitHub".
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            }
            else if (setup.GitHubAuthEnabled)
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            }
            else
            {
                options.DefaultScheme = ConsultantKeyAuthHandler.SchemeName;
            }
        });

        if (setup.GitHubAuthEnabled)
        {
            AddGitHubOAuth(authBuilder, setup);
        }

        if (setup.ConsultantAuthEnabled)
        {
            authBuilder.AddScheme<AuthenticationSchemeOptions, ConsultantKeyAuthHandler>(
                ConsultantKeyAuthHandler.SchemeName, null);
        }

        if (setup.GitHubAuthEnabled && setup.ConsultantAuthEnabled)
        {
            AddPolicyScheme(authBuilder);
        }

        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });

        return services;
    }

    private static void AddGitHubOAuth(
        AuthenticationBuilder authBuilder,
        AppAuthSetup setup)
    {
        authBuilder
            .AddCookie(options =>
            {
                options.LoginPath = "/api/auth/login";
                options.LogoutPath = "/api/auth/logout";
                options.Cookie.Name = "AgentAcademy.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(7);
                options.SlidingExpiration = true;

                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = 401;
                    return Task.CompletedTask;
                };
            })
            .AddOAuth("GitHub", options =>
            {
                options.ClientId = setup.GitHubClientId;
                options.ClientSecret = setup.GitHubClientSecret;
                options.CallbackPath = setup.GitHubCallbackPath;
                options.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
                options.TokenEndpoint = "https://github.com/login/oauth/access_token";
                options.UserInformationEndpoint = "https://api.github.com/user";
                options.Scope.Add("read:user");
                options.Scope.Add("user:email");
                options.Scope.Add("repo");
                options.SaveTokens = true;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

                options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
                options.ClaimActions.MapJsonKey(ClaimTypes.Name, "login");
                options.ClaimActions.MapJsonKey("urn:github:name", "name");
                options.ClaimActions.MapJsonKey("urn:github:avatar", "avatar_url");

                options.Events = new OAuthEvents
                {
                    OnCreatingTicket = async context =>
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);

                        using var response = await context.Backchannel.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            context.HttpContext.RequestAborted);
                        response.EnsureSuccessStatusCode();

                        var user = await response.Content.ReadFromJsonAsync<JsonElement>();
                        context.RunClaimActions(user);

                        if (!string.IsNullOrEmpty(context.AccessToken))
                        {
                            var tokenProvider = context.HttpContext.RequestServices
                                .GetRequiredService<ICopilotTokenProvider>();
                            // GitHub App refresh tokens are valid for 6 months (15,811,200 seconds).
                            var refreshTokenExpiry = !string.IsNullOrEmpty(context.RefreshToken)
                                ? TimeSpan.FromDays(180)
                                : (TimeSpan?)null;
                            tokenProvider.SetTokens(
                                context.AccessToken,
                                context.RefreshToken,
                                context.ExpiresIn,
                                refreshTokenExpiry);
                        }
                    }
                };
            });
    }

    private static void AddPolicyScheme(AuthenticationBuilder authBuilder)
    {
        authBuilder.AddPolicyScheme("MultiAuth", "Multi-Auth Policy", options =>
        {
            options.ForwardDefaultSelector = context =>
            {
                var header = context.Request.Headers[ConsultantKeyAuthHandler.HeaderName].ToString();
                if (!string.IsNullOrEmpty(header))
                    return ConsultantKeyAuthHandler.SchemeName;
                return CookieAuthenticationDefaults.AuthenticationScheme;
            };
        });
    }
}

﻿using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Smartstore.Core.Identity;
using Smartstore.Core.Seo;
using Smartstore.Utilities;

namespace Smartstore.Web.Api.Security
{
    /// <summary>
    /// Verifies the identity of a user using basic authentication.
    /// Also ensures that requests are sent via HTTPS.
    /// </summary>
    public sealed class BasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly SmartDbContext _db;
        private readonly IWebApiService _apiService;
        private readonly SignInManager<Customer> _signInManager;
        private readonly Lazy<IUrlService> _urlService;

        public BasicAuthenticationHandler(
            SmartDbContext db,
            IWebApiService apiService,
            SignInManager<Customer> signInManager,
            Lazy<IUrlService> urlService,
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            ISystemClock clock)
            : base(options, logger, encoder, clock)
        {
            _db = db;
            _apiService = apiService;
            _signInManager = signInManager;
            _urlService = urlService;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var state = _apiService.GetState();

            try
            {
                if (!state.IsActive)
                {
                    throw new AuthenticationException(AccessDeniedReason.ApiDisabled);
                }

                if (!Request.Scheme.EqualsNoCase(Uri.UriSchemeHttps) && !CommonHelper.IsDevEnvironment)
                {
                    throw new AuthenticationException(AccessDeniedReason.SslRequired);
                }

                var (customer, user) = await GetCustomer();

                await _signInManager.SignInAsync(customer, true);
                //$"Signed in using '{Scheme.Name}'.".Dump();

                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, customer.Id.ToString()),
                    new Claim(ClaimTypes.Name, customer.Username)
                };

                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
                var ticket = new AuthenticationTicket(principal, Scheme.Name);

                SetResponseHeaders(null, customer, state);

                return AuthenticateResult.Success(ticket);
            }
            catch (Exception ex)
            {
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                Response.HttpContext.Features.Set<IExceptionHandlerPathFeature>(new AuthenticationExceptionPathFeature(ex, Request));

                var policy = _urlService.Value.GetUrlPolicy();
                if (policy?.Endpoint == null)
                {
                    policy.Endpoint = Request.HttpContext.GetEndpoint();
                }

                SetResponseHeaders(ex, null, state);

                return AuthenticateResult.Fail(ex);
            }
        }

        private async Task<(Customer Customer, WebApiUser User)> GetCustomer()
        {
            var rawAuthValue = Request?.Headers["Authorization"];
            if (!AuthenticationHeaderValue.TryParse(rawAuthValue, out var authHeader) || authHeader?.Parameter == null)
            {
                throw new AuthenticationException(AccessDeniedReason.InvalidAuthorizationHeader);
            }

            var credentialsStr = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Parameter));
            if (!credentialsStr.SplitToPair(out var publicKey, out _, ":") || publicKey.IsEmpty())
            {
                throw new AuthenticationException(AccessDeniedReason.InvalidAuthorizationHeader);
            }

            var apiUsers = await _apiService.GetApiUsersAsync();
            if (!apiUsers.TryGetValue(publicKey, out var user) || user == null)
            {
                throw new AuthenticationException(AccessDeniedReason.UserUnknown, publicKey);
            }

            if (!user.Enabled)
            {
                throw new AuthenticationException(AccessDeniedReason.UserDisabled, publicKey);
            }

            if (credentialsStr != $"{user.PublicKey}:{user.SecretKey}")
            {
                throw new AuthenticationException(AccessDeniedReason.InvalidCredentials, publicKey);
            }

            var customer = await _db.Customers.FindByIdAsync(user.CustomerId, false);
            if (customer == null)
            {
                throw new AuthenticationException(AccessDeniedReason.UserUnknown, publicKey);
            }

            if (!customer.Active)
            {
                throw new AuthenticationException(AccessDeniedReason.UserInactive, publicKey);
            }

            user.LastRequest = DateTime.UtcNow;
            return (customer, user);
        }

        private void SetResponseHeaders(Exception ex, Customer customer, WebApiState state)
        {
            var headers = Response.Headers;

            headers.Add("Smartstore-Api-AppVersion", SmartstoreVersion.CurrentFullVersion);
            headers.Add("Smartstore-Api-Version", state.Version);
            headers.Add("Smartstore-Api-MaxTop", state.MaxTop.ToString());
            headers.Add("Smartstore-Api-Date", DateTime.UtcNow.ToString("o"));

            if (customer != null)
            {
                headers.Add("Smartstore-Api-CustomerId", customer.Id.ToString());
            }

            if (ex == null)
            {
                headers.CacheControl = "no-cache";
            }
            else
            {
                headers.WWWAuthenticate = "Basic realm=\"Smartstore.WebApi\", charset=\"UTF-8\"";

                if (ex is AuthenticationException authEx)
                {
                    headers.Add("Smartstore-Api-AuthResultId", ((int)authEx.DeniedReason).ToString());
                    headers.Add("Smartstore-Api-AuthResultDesc", authEx.DeniedReason.ToString());
                }
            }
        }
    }

    internal class AuthenticationExceptionPathFeature : IExceptionHandlerPathFeature
    {
        public AuthenticationExceptionPathFeature(Exception ex, HttpRequest request)
        {
            Error = ex;
            Path = request?.Path;
        }

        public Exception Error { get; }
        public string Path { get; }
    }
}
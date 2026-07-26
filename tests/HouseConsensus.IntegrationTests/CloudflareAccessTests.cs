using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HouseConsensus.Server.Auth;
using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HouseConsensus.IntegrationTests;

public sealed class CloudflareAccessTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Enabled_configuration_requires_valid_team_domain_and_audience()
    {
        var missingTeam = Config(("CloudflareAccess:Enabled", "true"), ("CloudflareAccess:Audience", "audience"));
        var malformedTeam = Config(("CloudflareAccess:Enabled", "true"), ("CloudflareAccess:TeamDomain", "https://team.cloudflareaccess.com/path"), ("CloudflareAccess:Audience", "audience"));
        var missingAudience = Config(("CloudflareAccess:Enabled", "true"), ("CloudflareAccess:TeamDomain", "team.cloudflareaccess.com"));
        var wildcardAudience = Config(("CloudflareAccess:Enabled", "true"), ("CloudflareAccess:TeamDomain", "team.cloudflareaccess.com"), ("CloudflareAccess:Audience", "*"));

        Assert.Throws<InvalidOperationException>(() => CloudflareAccessOptions.FromConfiguration(missingTeam));
        Assert.Throws<InvalidOperationException>(() => CloudflareAccessOptions.FromConfiguration(malformedTeam));
        Assert.Throws<InvalidOperationException>(() => CloudflareAccessOptions.FromConfiguration(missingAudience));
        Assert.Throws<InvalidOperationException>(() => CloudflareAccessOptions.FromConfiguration(wildcardAudience));
    }

    [Fact]
    public void Actual_production_application_fails_startup_when_Cloudflare_Access_is_disabled()
    {
        using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("CloudflareAccess:Enabled", "false");
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("required in Production", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Actual_production_application_fails_startup_for_wildcard_audience()
    {
        using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("CloudflareAccess:Enabled", "true");
            builder.UseSetting("CloudflareAccess:TeamDomain", "team.cloudflareaccess.com");
            builder.UseSetting("CloudflareAccess:Audience", "*");
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Audience", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Production_requires_Cloudflare_while_nonproduction_preserves_test_cookie_mode()
    {
        var options = CloudflareAccessOptions.FromConfiguration(Config());

        Assert.False(options.Enabled);
        Assert.Null(options.TeamDomain);
        Assert.Null(options.Audience);
        Assert.Throws<InvalidOperationException>(() => AuthenticationSetup.Add(new ServiceCollection(), Config(), true));
        var services = new ServiceCollection();
        services.AddLogging();
        AuthenticationSetup.Add(services, Config(), false);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("one.two")]
    [InlineData("one.two.three.four")]
    public async Task Validator_rejects_missing_and_malformed_tokens(string token)
    {
        using var rsa = RSA.Create(2048);
        var validator = Validator(rsa, out _);

        Assert.Null(await validator.ValidateAsync(token, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validator_accepts_only_valid_RS256_signature_issuer_audience_and_lifetime()
    {
        using var trusted = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var validator = Validator(trusted, out var jwks);
        var valid = Token(trusted);

        Assert.Equal("member@example.test", await validator.ValidateAsync(valid, TestContext.Current.CancellationToken));
        Assert.Equal("member@example.test", await validator.ValidateAsync(valid, TestContext.Current.CancellationToken));
        Assert.Equal(1, jwks.SendCount);
        Assert.Null(await validator.ValidateAsync(Token(trusted, algorithm: "HS256"), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(attacker), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(trusted, issuer: "https://other.cloudflareaccess.com"), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(trusted, audience: "wrong-audience"), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(trusted, expires: Now.AddSeconds(-1)), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(trusted, notBefore: Now.AddSeconds(1)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validator_rate_limits_unknown_key_refresh_and_fails_closed_on_malformed_JWKS()
    {
        using var trusted = RSA.Create(2048);
        var handler = new JwksHandler(trusted.ExportParameters(false));
        var validator = new CloudflareJwtValidator(new HttpClient(handler), new CloudflareAccessOptions(true, "team.cloudflareaccess.com", "expected-audience"), new FixedTimeProvider(Now));
        Assert.Null(await validator.ValidateAsync(Token(trusted, keyId: "unknown-1"), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(trusted, keyId: "unknown-2"), TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.SendCount);

        var malformed = new CloudflareJwtValidator(
            new HttpClient(new StaticJsonHandler("{\"keys\":[{\"kty\":\"RSA\",\"alg\":\"RS256\"}]}")),
            new CloudflareAccessOptions(true, "team.cloudflareaccess.com", "expected-audience"),
            new FixedTimeProvider(Now));
        Assert.Null(await malformed.ValidateAsync(Token(trusted), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validator_throttles_repeated_failed_JWKS_refresh_attempts()
    {
        using var trusted = RSA.Create(2048);
        var handler = new FailureHandler();
        var validator = new CloudflareJwtValidator(new HttpClient(handler), Options(), new FixedTimeProvider(Now));

        for (var i = 0; i < 10; i++)
            Assert.Null(await validator.ValidateAsync(Token(trusted, keyId: $"unknown-{i}"), TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.SendCount);

        var malformedHandler = new CountingJsonHandler("{\"keys\":[{\"kty\":\"RSA\",\"alg\":\"RS256\"}]}");
        var malformed = new CloudflareJwtValidator(new HttpClient(malformedHandler), Options(), new FixedTimeProvider(Now));
        for (var i = 0; i < 10; i++)
            Assert.Null(await malformed.ValidateAsync(Token(trusted, keyId: $"malformed-{i}"), TestContext.Current.CancellationToken));
        Assert.Equal(1, malformedHandler.SendCount);
    }

    [Fact]
    public async Task Validator_refreshes_rotated_keys_and_accepts_only_the_expected_audience_in_arrays()
    {
        using var first = RSA.Create(2048);
        using var rotated = RSA.Create(2048);
        var clock = new MutableTimeProvider(Now);
        var handler = new RotatingJwksHandler(first.ExportParameters(false), rotated.ExportParameters(false));
        var validator = new CloudflareJwtValidator(new HttpClient(handler), Options(), clock);

        Assert.Equal("member@example.test", await validator.ValidateAsync(Token(first), TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal("member@example.test", await validator.ValidateAsync(Token(rotated, keyId: "rotated-key", expires: Now.AddMinutes(15), audiences: ["other", "expected-audience"]), TestContext.Current.CancellationToken));
        Assert.Null(await validator.ValidateAsync(Token(rotated, keyId: "rotated-key", expires: Now.AddMinutes(15), audiences: ["other"]), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task Validator_retains_last_known_good_keys_when_refresh_has_no_usable_keys()
    {
        using var trusted = RSA.Create(2048);
        var clock = new MutableTimeProvider(Now);
        var handler = new ValidThenEmptyJwksHandler(trusted.ExportParameters(false));
        var validator = new CloudflareJwtValidator(new HttpClient(handler), Options(), clock);

        Assert.Equal("member@example.test", await validator.ValidateAsync(Token(trusted), TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await validator.ValidateAsync(Token(trusted, keyId: "unknown"), TestContext.Current.CancellationToken));
        Assert.Equal("member@example.test", await validator.ValidateAsync(Token(trusted, expires: Now.AddMinutes(15)), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task Validator_rejects_none_algorithm_and_missing_or_invalid_required_claims()
    {
        using var trusted = RSA.Create(2048);
        var validator = Validator(trusted, out _);
        var validClaims = Claims();
        var missingExp = Claims(); missingExp.Remove("exp");
        var missingNbf = Claims(); missingNbf.Remove("nbf");
        var missingEmail = Claims(); missingEmail.Remove("email");

        Assert.Null(await validator.ValidateAsync(UnsignedToken(new { alg = "none", kid = "test-key" }, validClaims), TestContext.Current.CancellationToken));
        foreach (var claims in new object[]
        {
            missingExp,
            Claims(exp: "not-a-number"),
            missingNbf,
            Claims(nbf: "not-a-number"),
            missingEmail,
            Claims(email: null),
            Claims(email: "not-an-email"),
        })
            Assert.Null(await validator.ValidateAsync(SignedToken(trusted, new { alg = "RS256", kid = "test-key" }, claims), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Authentication_registration_selects_cookie_when_disabled_and_Cloudflare_when_enabled()
    {
        var cookieServices = new ServiceCollection();
        cookieServices.AddLogging();
        AuthenticationSetup.Add(cookieServices, Config(), false);
        var cloudflareServices = new ServiceCollection();
        cloudflareServices.AddLogging();
        AuthenticationSetup.Add(cloudflareServices, Config(("CloudflareAccess:Enabled", "true"), ("CloudflareAccess:TeamDomain", "team.cloudflareaccess.com"), ("CloudflareAccess:Audience", "audience")), true);

        var cookieScheme = await cookieServices.BuildServiceProvider().GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        var cloudflareScheme = await cloudflareServices.BuildServiceProvider().GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();

        Assert.Equal("Cookies", cookieScheme!.Name);
        Assert.Equal(CloudflareAccessOptions.Scheme, cloudflareScheme!.Name);
    }

    [Fact]
    public async Task Authentication_handler_trusts_only_the_validated_assertion_and_preserves_member_claims()
    {
        var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner };
        var context = AuthenticationContext(new FakeValidator("owner@example.test"), new FakeMembers(owner));
        context.Request.Headers["Cf-Access-Jwt-Assertion"] = "valid-token";
        context.Request.Headers["Cf-Access-Authenticated-User-Email"] = "attacker@example.test";

        var result = await context.AuthenticateAsync(CloudflareAccessOptions.Scheme);

        Assert.True(result.Succeeded);
        Assert.Equal(owner.Id.ToString(), result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(owner.Email, result.Principal.FindFirstValue(ClaimTypes.Email));
        Assert.True(result.Principal.IsInRole(MemberRole.Owner.ToString()));
    }

    [Fact]
    public async Task Authentication_handler_rejects_missing_malformed_or_unknown_assertions()
    {
        var owner = new Member { Email = "owner2@example.test", Role = MemberRole.Owner };
        var missing = AuthenticationContext(new FakeValidator(null), new FakeMembers(owner));
        missing.Request.Headers["Cf-Access-Authenticated-User-Email"] = owner.Email;
        var malformed = AuthenticationContext(new FakeValidator(null), new FakeMembers(owner));
        malformed.Request.Headers["Cf-Access-Jwt-Assertion"] = "bad-token";
        var unknown = AuthenticationContext(new FakeValidator("unknown@example.test"), new FakeMembers(null));
        unknown.Request.Headers["Cf-Access-Jwt-Assertion"] = "valid-token";

        Assert.NotNull((await missing.AuthenticateAsync(CloudflareAccessOptions.Scheme)).Failure);
        Assert.NotNull((await malformed.AuthenticateAsync(CloudflareAccessOptions.Scheme)).Failure);
        Assert.NotNull((await unknown.AuthenticateAsync(CloudflareAccessOptions.Scheme)).Failure);
    }

    [Fact]
    public async Task Direct_origin_protected_API_and_SignalR_negotiate_reject_spoofed_identity_without_assertion()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddSingleton<ICloudflareJwtValidator>(new FakeValidator(null));
        builder.Services.AddSingleton<ICloudflareMemberService>(new FakeMembers(null));
        builder.Services.AddAuthentication(CloudflareAccessOptions.Scheme)
            .AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(CloudflareAccessOptions.Scheme, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/protected", () => Results.Ok()).RequireAuthorization();
        app.MapHub<ProtectedHub>("/hub");
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            client.DefaultRequestHeaders.Add("Cf-Access-Authenticated-User-Email", "attacker@example.test");

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/protected", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/hub/negotiate?negotiateVersion=1", new StringContent(""), TestContext.Current.CancellationToken)).StatusCode);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static DefaultHttpContext AuthenticationContext(ICloudflareJwtValidator validator, ICloudflareMemberService members)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(validator);
        services.AddSingleton(members);
        services.AddAuthentication(CloudflareAccessOptions.Scheme)
            .AddScheme<AuthenticationSchemeOptions, CloudflareAccessHandler>(CloudflareAccessOptions.Scheme, _ => { });
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private static CloudflareJwtValidator Validator(RSA rsa, out JwksHandler handler)
    {
        handler = new JwksHandler(rsa.ExportParameters(false));
        return new CloudflareJwtValidator(new HttpClient(handler), new CloudflareAccessOptions(true, "team.cloudflareaccess.com", "expected-audience"), new FixedTimeProvider(Now));
    }

    private static string Token(RSA rsa, string algorithm = "RS256", string issuer = "https://team.cloudflareaccess.com", string audience = "expected-audience", DateTimeOffset? expires = null, DateTimeOffset? notBefore = null, string keyId = "test-key", string[]? audiences = null)
    {
        object payload = audiences is null
            ? new { iss = issuer, aud = audience, exp = (expires ?? Now.AddMinutes(5)).ToUnixTimeSeconds(), nbf = (notBefore ?? Now.AddMinutes(-1)).ToUnixTimeSeconds(), email = "Member@Example.Test" }
            : new { iss = issuer, aud = audiences, exp = (expires ?? Now.AddMinutes(5)).ToUnixTimeSeconds(), nbf = (notBefore ?? Now.AddMinutes(-1)).ToUnixTimeSeconds(), email = "Member@Example.Test" };
        return SignedToken(rsa, new { alg = algorithm, kid = keyId, typ = "JWT" }, payload);
    }

    private static Dictionary<string, object?> Claims(object? exp = null, object? nbf = null, string? email = "Member@Example.Test") => new()
    {
        ["iss"] = "https://team.cloudflareaccess.com",
        ["aud"] = new[] { "expected-audience" },
        ["exp"] = exp ?? Now.AddMinutes(5).ToUnixTimeSeconds(),
        ["nbf"] = nbf ?? Now.AddMinutes(-1).ToUnixTimeSeconds(),
        ["email"] = email,
    };

    private static string SignedToken(RSA rsa, object headerValue, object payloadValue)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(headerValue));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payloadValue));
        var signingInput = $"{header}.{payload}";
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    private static string UnsignedToken(object headerValue, object payloadValue) =>
        $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(headerValue))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(payloadValue))}.AA";

    private static CloudflareAccessOptions Options() => new(true, "team.cloudflareaccess.com", "expected-audience");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    [Authorize]
    private sealed class ProtectedHub : Hub;

    private sealed class FakeValidator(string? email) : ICloudflareJwtValidator
    {
        public Task<string?> ValidateAsync(string? token, CancellationToken ct) => Task.FromResult(email);
    }

    private sealed class FakeMembers(Member? member) : ICloudflareMemberService
    {
        public Task<Member?> ResolveAsync(string email, CancellationToken ct) => Task.FromResult(member);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class JwksHandler(RSAParameters key) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var body = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test-key", use = "sig", alg = "RS256", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!) } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    private sealed class CountingJsonHandler(string json) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class FailureHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class ValidThenEmptyJwksHandler(RSAParameters key) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var body = SendCount == 1
                ? JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test-key", alg = "RS256", n = Base64Url(key.Modulus!), e = Base64Url(key.Exponent!) } } })
                : "{\"keys\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RotatingJwksHandler(RSAParameters first, RSAParameters rotated) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SendCount++;
            var key = SendCount == 1 ? ("test-key", first) : ("rotated-key", rotated);
            var body = JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = key.Item1, use = "sig", alg = "RS256", n = Base64Url(key.Item2.Modulus!), e = Base64Url(key.Item2.Exponent!) } } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}

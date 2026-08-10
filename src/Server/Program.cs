using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Data;
using HouseConsensus.Server.Listings;
using HouseConsensus.Server.Learning;
using HouseConsensus.Server.Scoring;
using HouseConsensus.Server.Hubs;
using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System.Threading.RateLimiting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, services, logger) =>
{
    logger
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "HouseConsensus");
    var seqUrl = context.Configuration["Seq:Url"];
    if (!string.IsNullOrWhiteSpace(seqUrl)) logger.WriteTo.Seq(seqUrl);
});
var debugAutoLogin = builder.Configuration.GetValue("Debug:AutoLogin", false);
var e2eTestAuth = builder.Configuration.GetValue("E2E:TestAuth", false);
var e2eSeedData = builder.Configuration.GetValue("E2E:SeedData", false);
DebugAutoLoginMiddleware.EnsureSafe(debugAutoLogin, builder.Environment.EnvironmentName);
DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(e2eTestAuth, debugAutoLogin, e2eSeedData, builder.Environment.EnvironmentName);
var databaseConnectionString = builder.Configuration.GetConnectionString("Database") ?? "Host=postgres;Database=house_consensus;Username=house_consensus;Password=house_consensus";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(databaseConnectionString, n => n.MapEnum<MemberRole>("member_role").MapEnum<VoteChoice>("vote_choice").MapEnum<ListingState>("listing_state").MapEnum<ReasonTag>("reason_tag").MapEnum<VoteCategory>("vote_category").MapEnum<CategoryRating>("category_rating").MapEnum<OverrideAction>("override_action")));
builder.Services.AddScoped(_ => new PostgresManualScoringStore(databaseConnectionString));
var cloudflareAccess = AuthenticationSetup.Add(builder.Services, builder.Configuration, builder.Environment.IsProduction());
if (e2eTestAuth && !cloudflareAccess.Enabled) builder.Services.AddScoped<ICloudflareMemberService, CloudflareMemberService>();
builder.Services.AddAuthorization(o => o.AddPolicy("owner", p => p.RequireClaim(ClaimTypes.Role, MemberRole.Owner.ToString())));
builder.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; o.ForwardLimit = 1; });
var magicRequestPermitLimit = builder.Configuration.GetValue("Auth:MagicRequestPermitLimit", 5);
var magicConsumePermitLimit = builder.Configuration.GetValue("Auth:MagicConsumePermitLimit", 20);
var listingLookupPermitLimit = builder.Configuration.GetValue("Listings:LookupPermitLimit", 12);
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = StatusCodes.Status429TooManyRequests; o.AddPolicy("magic-request", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = magicRequestPermitLimit, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 })); o.AddPolicy("magic-consume", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = magicConsumePermitLimit, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 })); o.AddPolicy("listing-lookup", context => RateLimitPartition.GetFixedWindowLimiter(context.User.Identity?.IsAuthenticated == true ? context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated" : context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = listingLookupPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })); });
builder.Services.AddSignalR(); builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddScoped<MagicLinkService>(); builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 128 * 1024 * 1024); builder.Services.AddHttpClient<ListingImageService>(client => { client.Timeout = TimeSpan.FromSeconds(15); client.DefaultRequestHeaders.UserAgent.ParseAdd("HouseConsensus/1.0"); });
builder.Services.AddHttpClient<BoligsidenListingLookup>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
if (builder.Environment.IsDevelopment() && e2eSeedData)
    builder.Services.AddScoped<IAiRuleGenerator, E2EAiRuleGenerator>();
else
    builder.Services.AddHttpClient<IAiRuleGenerator, OllamaAiRuleGenerator>(client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddScoped<AiLearningService>();
var app = builder.Build();
app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
if (app.Environment.IsProduction()) { app.UseHsts(); if (!cloudflareAccess.Enabled) app.UseHttpsRedirection(); }
app.UseExceptionHandler(e => e.Run(async c => { c.Response.StatusCode = 500; await c.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." }); }));
app.UseBlazorFrameworkFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var name = Path.GetFileName(context.Context.Request.Path.Value);
        if (name is "service-worker.js" or "service-worker-assets.js" or "app.css")
            context.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
    }
});
app.Use(async (context, next) => { var unsafeApiRequest = context.Request.Path.StartsWithSegments("/api") && (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)); if (unsafeApiRequest && context.Request.Headers["X-House-Consensus-CSRF"] != "1") { context.Response.StatusCode = StatusCodes.Status400BadRequest; await context.Response.WriteAsJsonAsync(new { error = "Missing same-origin request header." }); return; } await next(); });
app.UseAuthentication();
if (debugAutoLogin) app.UseMiddleware<DebugAutoLoginMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.MapHealthChecks("/health"); app.MapHub<ConsensusHub>("/hubs/consensus");
app.MapGet("/api/version", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(RunningBuildVersion());
}).AllowAnonymous();

var auth = app.MapGroup("/api/auth");
auth.MapGet("/mode", () => Results.Ok(new AuthModeDto(cloudflareAccess.Enabled))).AllowAnonymous();
if (!cloudflareAccess.Enabled)
{
    auth.MapPost("/request", async (RequestMagicLink request, MagicLinkService links, CancellationToken ct) => { if (!IsEmail(request.Email)) return Results.BadRequest(new { error = "Invalid email." }); await links.RequestAsync(request.Email, ct); return Results.Accepted(value: new { message = "If the address is eligible, a link has been sent." }); }).AllowAnonymous().RequireRateLimiting("magic-request");
    auth.MapGet("/consume", async (string token, MagicLinkService links, HttpContext context, CancellationToken ct) => { var member = await links.ConsumeAsync(token, ct); if (member is null) return Results.BadRequest(new { error = "Invalid or expired link." }); await SignIn(context, member); return Results.Redirect("/"); }).AllowAnonymous().RequireRateLimiting("magic-consume");
}
auth.MapPost("/logout", async (HttpContext c) => { if (cloudflareAccess.Enabled) { c.Response.Headers["X-House-Consensus-Logout"] = "/cdn-cgi/access/logout"; return Results.NoContent(); } await c.SignOutAsync(); return Results.NoContent(); }).RequireAuthorization();
auth.MapGet("/me", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var m = await db.Members.FindAsync([user.MemberId()], ct); return m is null ? Results.NotFound() : Results.Ok(ToMemberDto(m)); }).RequireAuthorization();
auth.MapPut("/language", async (UpdateLanguage request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var m = await db.Members.FindAsync([user.MemberId()], ct); if (m is null) return Results.NotFound(); try { m.SetLanguage(request.Language); await db.SaveChangesAsync(ct); return Results.Ok(ToMemberDto(m)); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } }).RequireAuthorization();
auth.MapPut("/profile", async (UpdateProfile request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var m = await db.Members.FindAsync([user.MemberId()], ct); if (m is null) return Results.NotFound(); try { m.SetProfile(request.DisplayName, request.AvatarColor); await db.SaveChangesAsync(ct); return Results.Ok(ToMemberDto(m)); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } }).RequireAuthorization();

var members = app.MapGroup("/api/members").RequireAuthorization("owner");
members.MapGet("/", async (AppDbContext db, CancellationToken ct) => (await db.Members.OrderBy(x => x.Email).ToListAsync(ct)).Select(ToMemberDto));
members.MapPost("/voting-identities/preview", async (CombineVotingIdentities request, AppDbContext db, CancellationToken ct) =>
{
    var affected = await ResolveCombinationMembers(request, db, ct); if (affected is null) return Results.BadRequest(new { error = "Choose a primary and complete, non-overlapping voting identities." });
    var ids = affected.Select(x => x.Id).ToArray();
    var conflicts = await db.Votes.Where(x => ids.Contains(x.MemberId)).GroupBy(x => x.ListingId).Where(x => x.Select(v => v.MemberId).Distinct().Count() > 1).Select(x => new { x.Key, MemberIds = x.Select(v => v.MemberId).Distinct().ToArray() }).ToListAsync(ct);
    var listingIds = conflicts.Select(x => x.Key).ToArray(); var addresses = await db.Listings.Where(x => listingIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Address, ct);
    return Results.Ok(new VotingIdentityPreview(affected.Select(ToMemberDto).ToArray(), conflicts.Select(x => new VotingIdentityConflict(x.Key, addresses.GetValueOrDefault(x.Key, ""), x.MemberIds)).ToArray()));
});
members.MapPost("/voting-identities/combine", async (CombineVotingIdentities request, AppDbContext db, IHubContext<ConsensusHub> hub, CancellationToken ct) =>
{
    var affected = await ResolveCombinationMembers(request, db, ct); if (affected is null) return Results.BadRequest(new { error = "Choose a primary and complete, non-overlapping voting identities." });
    foreach (var member in affected) member.VotingIdentityId = request.PrimaryMemberId;
    await db.SaveChangesAsync(ct); await hub.Clients.All.SendAsync("MembershipChanged", request.PrimaryMemberId, true, ct); return Results.NoContent();
});
members.MapPost("/{id:guid}/voting-identity/separate", async (Guid id, AppDbContext db, IHubContext<ConsensusHub> hub, CancellationToken ct) =>
{
    var member = await db.Members.FindAsync([id], ct); if (member is null) return Results.NotFound(); member.VotingIdentityId = member.Id; await db.SaveChangesAsync(ct); await hub.Clients.All.SendAsync("MembershipChanged", id, member.IsActive, ct); return Results.NoContent();
});

var listings = app.MapGroup("/api/listings").RequireAuthorization();
listings.MapPost("/preview", async (FetchManualListing request, BoligsidenListingLookup lookup, CancellationToken ct) =>
{
    var preview = await lookup.ResolveAsync(request.Url, ct);
    return preview is null ? Results.NotFound(new { error = "No active Boligsiden listing was found for that address." }) : Results.Ok(preview);
}).RequireRateLimiting("listing-lookup");
listings.MapPost("/", async (CreateManualListing request, ClaimsPrincipal user, AppDbContext db, PostgresManualScoringStore scoringStore, IHubContext<ConsensusHub> hub, BoligsidenListingLookup lookup, TimeProvider clock, CancellationToken ct) =>
{
    var memberId = user.MemberId();
    if (!await db.Members.AnyAsync(x => x.Id == memberId && x.IsActive, ct)) return Results.Forbid();
    try
    {
        var fetched = await lookup.ResolveAsync(request.Url, ct)
            ?? await lookup.ResolveAddressAsync(request.Url, request.Address, request.City, ct);
        var submittedAddress = fetched?.Address ?? request.Address;
        var submittedCity = fetched?.City ?? request.City;
        var submittedPrice = fetched?.AskingPrice ?? request.AskingPrice;
        var url = ManualListing.NormalizeUrl(request.Url);
        var address = ManualListing.NormalizeAddress(submittedAddress);
        if (submittedPrice < 0 || submittedPrice > ManualListing.MaxAskingPrice || submittedCity?.Trim().Length > 200) return Results.BadRequest(new { error = "Optional listing details are invalid." });
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("LOCK TABLE listings IN SHARE ROW EXCLUSIVE MODE", ct);
        var existing = await FindManualDuplicate(url, address, db, ct);
        if (existing is not null)
        {
            if (existing.IsManuallyAdded && existing.ManualScoringCompletedAt is null)
            {
                existing.ManualScoringRequestedAt ??= clock.GetUtcNow();
                existing.ManualScoringError = null;
                await db.SaveChangesAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return Results.Ok(new ManualListingResult(existing.Id, true));
        }
        var listing = Listing.CreateManual(url, submittedAddress, memberId, clock.GetUtcNow());
        listing.ManualScoringRequestedAt = clock.GetUtcNow();
        listing.City = string.IsNullOrWhiteSpace(submittedCity) ? null : submittedCity.Trim();
        listing.Price = submittedPrice;
        if (fetched is not null)
        {
            listing.PostalCode = fetched.PostalCode; listing.LivingArea = fetched.LivingArea; listing.LotArea = fetched.LotArea;
            listing.Rooms = fetched.Rooms; listing.Floors = fetched.Floors; listing.Bathrooms = fetched.Bathrooms;
            listing.YearBuilt = fetched.YearBuilt; listing.EnergyLabel = fetched.EnergyLabel; listing.MonthlyExpense = fetched.MonthlyExpense;
            listing.DaysOnMarket = fetched.DaysOnMarket; listing.PreviewImageUrl = fetched.PreviewImageUrl;
            listing.Latitude = fetched.Latitude; listing.Longitude = fetched.Longitude;
        }
        db.Listings.Add(listing);
        await db.SaveChangesAsync(ct);
        await scoringStore.EnqueueAsync((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction(), listing.Id, listing.ExternalId, listing.CanonicalUrl!, listing.ManualScoringRequestedAt!.Value, ct);
        await transaction.CommitAsync(ct);
        await hub.Clients.All.SendAsync("ListingStateChanged", listing.Id, listing.State, ct);
        return Results.Created($"/api/listings/{listing.Id}", new ManualListingResult(listing.Id, false));
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).RequireRateLimiting("listing-lookup");
listings.MapGet("/queue", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var all = await ListingDtos(db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored).OrderByDescending(x => x.IsManuallyAdded).ThenByDescending(x => x.ManuallyAddedAt), db, user, ct); var identity = await VotingIdentityFor(user.MemberId(), db, ct); return all.Where(x => !x.Votes.Any(v => (v.EffectiveMemberId ?? v.MemberId) == identity && v.Choice != VoteChoice.NotVoted)).OrderByDescending(x => x.IsManuallyAdded).ThenByDescending(x => x.ManuallyAddedAt).ThenByDescending(x => x.TrustedFamilyFitScore.HasValue).ThenByDescending(x => x.TrustedFamilyFitScore); });
listings.MapGet("/browse", async (string? city, decimal? minPrice, decimal? maxPrice, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var q = db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored); if (!string.IsNullOrWhiteSpace(city)) q = q.Where(x => x.City != null && EF.Functions.ILike(x.City, $"%{city}%")); if (minPrice.HasValue) q = q.Where(x => x.Price >= minPrice); if (maxPrice.HasValue) q = q.Where(x => x.Price <= maxPrice); var all = await ListingDtos(q.OrderByDescending(x => x.IsManuallyAdded).ThenByDescending(x => x.ManuallyAddedAt), db, user, ct); return all.OrderByDescending(x => x.IsManuallyAdded).ThenByDescending(x => x.ManuallyAddedAt).ThenByDescending(x => x.TrustedFamilyFitScore.HasValue).ThenByDescending(x => x.TrustedFamilyFitScore); });
listings.MapGet("/consensus", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var all = await ListingDtos(db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored), db, user, ct); return all.Where(x => x.Consensus); });
listings.MapGet("/my-votes", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
{
    var id = await VotingIdentityFor(user.MemberId(), db, ct); var accountIds = await db.Members.Where(x => x.VotingIdentityId == id).Select(x => x.Id).ToListAsync(ct);
    var votes = await db.Votes.Include(x => x.Ratings).Where(x => accountIds.Contains(x.MemberId)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct);
    var latest = votes.GroupBy(x => x.ListingId).Select(g => g.First()).Where(x => x.Choice != VoteChoice.NotVoted);
    return await VoteDtos(latest, db, ct);
});
listings.MapGet("/{id:guid}/image", async (Guid id, ClaimsPrincipal user, AppDbContext db, ListingImageService images, CancellationToken ct) => { var listing = await db.Listings.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.State, x.PreviewImageUrl }).SingleOrDefaultAsync(ct); if (listing is null || (listing.State == ListingState.Archived && !user.IsInRole(MemberRole.Owner.ToString()))) return Results.NotFound(); var image = await images.GetAsync(id, listing.PreviewImageUrl, ct); return image is null ? Results.NotFound() : Results.Bytes(image.Bytes, image.ContentType); });
listings.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var listing = await db.Listings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (listing is null) return Results.NotFound(); if (listing.State == ListingState.Archived && !user.IsInRole(MemberRole.Owner.ToString())) return Results.NotFound(); var all = await ListingDtos(db.Listings.Where(x => x.Id == id), db, user, ct); return Results.Ok(all[0]); });
listings.MapPost("/{id:guid}/votes", async (Guid id, CastVote request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) =>
{
    if (request.Choice == VoteChoice.NotVoted) return Results.BadRequest(new { error = "Clearing votes is not supported." });
    try
    {
        var vote = new Vote(id, user.MemberId(), (request.Ratings ?? []).Select(x => new VoteRating { Category = x.Category, Rating = x.Rating }), request.OverallScore, request.Note, clock.GetUtcNow());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockListingMutation(id, db, ct);
        if (!await db.Listings.AnyAsync(x => x.Id == id && (x.State == ListingState.Active || x.State == ListingState.Restored), ct)) return Results.NotFound();
        db.Votes.Add(vote); await db.SaveChangesAsync(ct);
        var consensus = await HasConsensus(id, db, ct); var dto = (await VoteDtos([vote], db, ct))[0];
        await transaction.CommitAsync(ct);
        await hub.Clients.Group($"listing:{id}").SendAsync("VoteChanged", dto, consensus, ct); await hub.Clients.All.SendAsync("ConsensusChanged", id, consensus, ct);
        return Results.Ok(new { vote = dto, consensus });
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
listings.MapGet("/{id:guid}/votes/history", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
{
    if (!await CanAccessListing(id, user, db, ct)) return Results.NotFound();
    var history = await db.Votes.Include(x => x.Ratings).Where(x => x.ListingId == id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct);
    return Results.Ok(await VoteDtos(history, db, ct));
});
listings.MapPut("/{id:guid}/votes/note", async (Guid id, EditVoteNote request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) =>
{
    var memberId = user.MemberId();
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await LockListingMutation(id, db, ct);
    if (!await CanAccessListing(id, user, db, ct)) return Results.NotFound();
    var vote = await db.Votes.Include(x => x.Ratings).Include(x => x.NoteRevisions).Where(x => x.ListingId == id && x.MemberId == memberId && x.Choice != VoteChoice.NotVoted).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
    if (vote is null) return Results.NotFound();
    try
    {
        vote.EditNote(memberId, request.Note, clock.GetUtcNow()); await db.SaveChangesAsync(ct);
        var consensus = await HasConsensus(id, db, ct); var dto = (await VoteDtos([vote], db, ct))[0]; await transaction.CommitAsync(ct);
        await hub.Clients.Group($"listing:{id}").SendAsync("VoteChanged", dto, consensus, ct); return Results.Ok(dto);
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
listings.MapGet("/{id:guid}/comments", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => !await CanAccessListing(id, user, db, ct) ? Results.NotFound() : Results.Ok(await db.Comments.Where(x => x.ListingId == id).OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.AuthorId, x.Body, x.IsDeleted, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct)));
listings.MapPost("/{id:guid}/comments", async (Guid id, AddComment request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) =>
{
    try
    {
        var c = new Comment(id, user.MemberId(), request.Body, clock.GetUtcNow());
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockListingMutation(id, db, ct);
        if (!await CanAccessListing(id, user, db, ct)) return Results.NotFound();
        db.Comments.Add(c); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await hub.Clients.Group($"listing:{id}").SendAsync("CommentChanged", c.Id, "created", ct);
        return Results.Created($"/api/comments/{c.Id}", new { c.Id, c.Body, c.AuthorId, c.CreatedAt });
    }
    catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

listings.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await LockListingMutation(id, db, ct);
    var listing = await db.Listings.FromSqlInterpolated($"SELECT * FROM listings WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
    if (listing is null || !listing.IsManuallyAdded) return Results.NotFound();
    var activity = await db.Votes.AnyAsync(x => x.ListingId == id, ct) || await db.Comments.IgnoreQueryFilters().AnyAsync(x => x.ListingId == id, ct) || await db.Feedback.AnyAsync(x => x.ListingId == id, ct) || await db.ListingOverrides.AnyAsync(x => x.ListingId == id, ct);
    var owner = user.IsInRole(MemberRole.Owner.ToString()); if ((!activity && listing.ManuallyAddedById != user.MemberId() && !owner) || (activity && !owner)) return Results.Forbid();
    listing.Archive(clock.GetUtcNow()); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); await hub.Clients.All.SendAsync("ListingStateChanged", id, listing.State, ct); return Results.NoContent();
});

var comments = app.MapGroup("/api/comments").RequireAuthorization();
comments.MapPut("/{id:guid}", async (Guid id, EditComment request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => await ChangeComment(id, user, db, hub, clock, request.Body, false, ct));
comments.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => await ChangeComment(id, user, db, hub, clock, null, true, ct));

var review = app.MapGroup("/api/review").RequireAuthorization("owner");
review.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => await ListingDtos(db.Listings.Where(x => x.State == ListingState.AiRejected).OrderByDescending(x => x.AiConfidence), db, user, ct));
review.MapPost("/{id:guid}/override", async (Guid id, ApplyListingOverride request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await LockListingMutation(id, db, ct);
    var l = await db.Listings.Include(x => x.Overrides).SingleOrDefaultAsync(x => x.Id == id, ct); if (l is null) return Results.NotFound();
    l.ApplyOverride(request.Action, user.MemberId(), request.Reason, clock.GetUtcNow()); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    await hub.Clients.All.SendAsync("ListingStateChanged", id, l.State, ct); return Results.Ok(new { l.Id, l.State });
});

var feedback = app.MapGroup("/api/feedback").RequireAuthorization();
feedback.MapPost("/", async (SubmitFeedback request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 4000) return Results.BadRequest(new { error = "Feedback must be 1-4000 characters." });
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    if (request.ListingId.HasValue)
    {
        await LockListingMutation(request.ListingId.Value, db, ct);
        if (!await CanAccessListing(request.ListingId.Value, user, db, ct)) return Results.BadRequest(new { error = "Unknown listing." });
    }
    var x = new Feedback { MemberId = user.MemberId(), ListingId = request.ListingId, Body = request.Body.Trim() }; db.Feedback.Add(x); await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
    return Results.Created($"/api/feedback/{x.Id}", x);
});
feedback.MapGet("/", [Authorize(Policy = "owner")] async (AppDbContext db, CancellationToken ct) => await db.Feedback.OrderByDescending(x => x.CreatedAt).ToListAsync(ct));
feedback.MapPut("/{id:guid}/review", [Authorize(Policy = "owner")] async (Guid id, ReviewFeedback request, AppDbContext db, TimeProvider clock, CancellationToken ct) => { var x = await db.Feedback.FindAsync([id], ct); if (x is null) return Results.NotFound(); x.ReviewedAt = request.Reviewed ? clock.GetUtcNow() : null; await db.SaveChangesAsync(ct); return Results.Ok(x); });
feedback.MapGet("/export.csv", [Authorize(Policy = "owner")] async (AppDbContext db, CancellationToken ct) => { var rows = await db.Feedback.OrderBy(x => x.CreatedAt).ToListAsync(ct); var csv = new StringBuilder("id,member_id,listing_id,body,created_at,reviewed_at\n"); foreach (var x in rows) csv.AppendLine(string.Join(',', x.Id, x.MemberId, x.ListingId?.ToString() ?? "", Csv(x.Body), x.CreatedAt.ToString("O", CultureInfo.InvariantCulture), x.ReviewedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "")); return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", "feedback.csv"); });
feedback.MapGet("/export.json", [Authorize(Policy = "owner")] async (AppDbContext db, CancellationToken ct) => Results.Json(await db.Feedback.OrderBy(x => x.CreatedAt).ToListAsync(ct)));

app.MapGet("/api/learning/proposals", async (AppDbContext db, CancellationToken ct) => (await db.AiRuleProposals.AsNoTracking().OrderByDescending(x => x.Version).ToListAsync(ct)).Select(ToAiRuleProposalDto)).RequireAuthorization("owner");
app.MapPost("/api/learning/proposals", async (ClaimsPrincipal user, AiLearningService learning, CancellationToken ct) => { try { return Results.Ok(ToAiRuleProposalDto(await learning.CreateProposalAsync(user.MemberId(), ct))); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } catch (HttpRequestException ex) { return Results.Json(new { error = $"AI proposal service unavailable: {ex.Message}" }, statusCode: 503); } catch (JsonException ex) { return Results.Json(new { error = $"AI proposal was invalid: {ex.Message}" }, statusCode: 502); } catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: 503); } }).RequireAuthorization("owner");
app.MapPost("/api/learning/{id:guid}/approve", async (Guid id, ClaimsPrincipal user, AiLearningService learning, CancellationToken ct) => { try { var proposal = await learning.ApproveAsync(id, user.MemberId(), ct); return proposal is null ? Results.NotFound() : Results.Ok(ToAiRuleProposalDto(proposal)); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } }).RequireAuthorization("owner");
app.MapPost("/api/learning/{id:guid}/reject", async (Guid id, ClaimsPrincipal user, AiLearningService learning, CancellationToken ct) => { try { var proposal = await learning.RejectAsync(id, user.MemberId(), ct); return proposal is null ? Results.NotFound() : Results.Ok(ToAiRuleProposalDto(proposal)); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } }).RequireAuthorization("owner");
app.MapPost("/api/learning/{id:guid}/deactivate", async (Guid id, ClaimsPrincipal user, AiLearningService learning, CancellationToken ct) => { var proposal = await learning.DeactivateAsync(id, user.MemberId(), ct); return proposal is null ? Results.NotFound() : Results.Ok(ToAiRuleProposalDto(proposal)); }).RequireAuthorization("owner");

if (!app.Environment.IsProduction() && app.Configuration.GetValue("E2E:SeedData", false))
{
    app.MapPost("/api/e2e/reset-review-listing", async (AppDbContext db, CancellationToken ct) =>
    {
        var listing = await db.Listings.SingleAsync(x => x.ExternalId == "e2e-rejected", ct);
        await db.ListingOverrides.Where(x => x.ListingId == listing.Id).ExecuteDeleteAsync(ct);
        listing.ApplyImportDecision(true);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }).RequireAuthorization("owner");
    app.MapPost("/api/e2e/reset-household-votes", async (AppDbContext db, CancellationToken ct) =>
    {
        await E2EDataSeeder.ResetHouseholdVotesAsync(db, ct);
        return Results.NoContent();
    }).RequireAuthorization("owner");
}

app.Map("/api/{**path}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
await Bootstrap(app);
app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "House Consensus terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static async Task Bootstrap(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Configuration.GetValue("Database:AutoMigrate", true))
        await db.Database.MigrateAsync();
    await db.Database.OpenConnectionAsync();
    await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
    var owner = MagicLinkService.Normalize(app.Configuration["INITIAL_OWNER_EMAIL"] ?? ""); if (!string.IsNullOrWhiteSpace(owner) && !await db.Members.AnyAsync()) { db.Members.Add(new Member { Email = owner, Role = MemberRole.Owner }); await db.SaveChangesAsync(); }
    if (!app.Environment.IsProduction() && app.Configuration.GetValue("E2E:SeedData", false)) await E2EDataSeeder.SeedAsync(db);
}
static bool IsEmail(string value) => System.Net.Mail.MailAddress.TryCreate(value, out var parsed) && parsed.Address == value.Trim();
static async Task SignIn(HttpContext c, Member m) { var claims = new[] { new Claim(ClaimTypes.NameIdentifier, m.Id.ToString()), new Claim(ClaimTypes.Email, m.Email), new Claim(ClaimTypes.Role, m.Role.ToString()) }; await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) }); }
static MemberDto ToMemberDto(Member m) => new(m.Id, m.Email, m.DisplayName, m.Language, m.Role, m.IsActive, AvatarColor.Resolve(m.AvatarColor, m.Id), m.VotingIdentityId == Guid.Empty ? m.Id : m.VotingIdentityId, m.VotingIdentityId == Guid.Empty || m.VotingIdentityId == m.Id);
static VoteDto ToVoteDto(Vote v, string memberInitials = "", string memberColor = "", Guid? effectiveId = null, string effectiveName = "", string? viaName = null) => new(v.Id, v.ListingId, v.MemberId, v.Choice, v.Tags, v.CreatedAt, v.Note, memberInitials, memberColor, v.Ratings.Select(x => new VoteRatingDto(x.Category, x.Rating)).ToArray(), v.Total, v.OverallScore, effectiveId ?? v.MemberId, effectiveName, viaName);
static async Task<List<VoteDto>> VoteDtos(IEnumerable<Vote> source, AppDbContext db, CancellationToken ct)
{
    var votes = source.ToList();
    var ids = votes.Select(x => x.MemberId).Distinct().ToArray();
    var members = await db.Members.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(ct);
    var identityIds = members.Select(x => x.VotingIdentityId == Guid.Empty ? x.Id : x.VotingIdentityId).Distinct().ToArray();
    var primaries = await db.Members.AsNoTracking().Where(x => identityIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
    return votes.Select(v => { var source = members.Single(x => x.Id == v.MemberId); var identityId = source.VotingIdentityId == Guid.Empty ? source.Id : source.VotingIdentityId; var primary = primaries.GetValueOrDefault(identityId) ?? source; var primaryName = DisplayName(primary); var sourceName = DisplayName(source); return ToVoteDto(v, AvatarInitials.From(primary.DisplayName, primary.Email), AvatarColor.Resolve(primary.AvatarColor, primary.Id), identityId, primaryName, source.Id == primary.Id ? null : sourceName); }).ToList();
}
static AiRuleProposalDto ToAiRuleProposalDto(AiRuleProposal x) { var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true }; var impact = JsonSerializer.Deserialize<AiRuleImpactDto>(x.ImpactPreviewJson, options) ?? new(0, 0, 0, 0, 0, []); var notes = JsonSerializer.Deserialize<List<AiRuleSourceNoteDto>>(x.SupportingNotesJson, options) ?? []; return new(x.Id, x.Version, x.VersionLabel, x.Summary, x.RuleJson, impact, notes, x.Status, x.IsActive, x.CreatedAt, x.ReviewedAt); }
static async Task<List<ListingDto>> ListingDtos(IQueryable<Listing> query, AppDbContext db, ClaimsPrincipal user, CancellationToken ct) { var items = await query.AsNoTracking().ToListAsync(ct); var ids = items.Select(x => x.Id).ToArray(); var votes = await db.Votes.AsNoTracking().Include(x => x.Ratings).Where(x => ids.Contains(x.ListingId)).ToListAsync(ct); var members = await db.Members.AsNoTracking().Select(x => new { x.Id, x.Email, x.DisplayName, x.AvatarColor, x.IsActive, x.VotingIdentityId }).ToListAsync(ct); var identityMap = members.ToDictionary(x => x.Id, x => x.VotingIdentityId == Guid.Empty ? x.Id : x.VotingIdentityId); var active = members.Where(x => x.IsActive).Select(x => identityMap[x.Id]).Distinct().ToList(); var initials = members.ToDictionary(x => x.Id, x => AvatarInitials.From(x.DisplayName, x.Email)); var colors = members.ToDictionary(x => x.Id, x => AvatarColor.Resolve(x.AvatarColor, x.Id)); var commentIds = await db.Comments.IgnoreQueryFilters().AsNoTracking().Where(x => ids.Contains(x.ListingId)).Select(x => x.ListingId).Distinct().ToListAsync(ct); var feedbackIds = await db.Feedback.AsNoTracking().Where(x => x.ListingId.HasValue && ids.Contains(x.ListingId.Value)).Select(x => x.ListingId!.Value).Distinct().ToListAsync(ct); var overrideIds = await db.ListingOverrides.AsNoTracking().Where(x => ids.Contains(x.ListingId)).Select(x => x.ListingId).Distinct().ToListAsync(ct); var effectiveVoteDtos = await VoteDtos(votes.GroupBy(x => x.ListingId).SelectMany(g => ConsensusRules.LatestVotes(g, identityMap).Values), db, ct); return items.Select(x => { var vs = votes.Where(v => v.ListingId == x.Id).ToList(); var hasActivity = vs.Count != 0 || commentIds.Contains(x.Id) || feedbackIds.Contains(x.Id) || overrideIds.Contains(x.Id); return new ListingDto(x.Id, x.ExternalId, x.Address, x.City, x.Price, x.FamilyFitScore, x.State, x.AiAssessed, x.AiConfidence, x.AiEvidence, x.ModelVersion, x.RuleVersion, x.SourceUrl, ConsensusRules.HasConsensus(active, vs, identityMap), effectiveVoteDtos.Where(v => v.ListingId == x.Id).ToArray(), x.PreviewImageUrl, x.LivingArea, x.LotArea, x.Rooms, x.YearBuilt, x.Bathrooms, x.Bedrooms, x.Floors, x.EnergyLabel, x.Quiet, x.BuildableHeadroom, x.GroundFloorBedroom, x.SeparateEntrance, x.SecondKitchen, x.PrivacyScore, x.FamilyPrivacyScore, x.KidsSpaceScore, x.GardenScore, x.SharedLivingScore, x.PracticalScore, x.FamilyPrivacyWeight, x.KidsSpaceWeight, x.GardenWeight, x.SharedLivingWeight, x.PracticalWeight, x.Latitude, x.Longitude, x.MonthlyExpense, x.DaysOnMarket, x.CommuteMinutes, x.BuildableStatus, x.Condition, x.GardenOrientation, x.MultigenFit, x.ImportedAt, x.PostalCode, x.Preferred, x.IsNew, x.FamilyUnits, x.CommuteJson, x.FirstSeenAt, x.RoadNoiseDb, x.RailNoiseDb, x.AirNoiseDb, x.IsManuallyAdded, x.ManuallyAddedById, x.ManuallyAddedById.HasValue ? members.Where(m => m.Id == x.ManuallyAddedById.Value).Select(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Email : m.DisplayName).FirstOrDefault() : null, x.ManuallyAddedAt, x.IsManuallyAdded && x.State != ListingState.Archived && !user.IsInRole(MemberRole.Owner.ToString()) && !hasActivity && x.ManuallyAddedById == user.MemberId(), x.IsManuallyAdded && x.State != ListingState.Archived && user.IsInRole(MemberRole.Owner.ToString()), x.RoadNoiseStatus, x.RoadNoiseLnightDb, x.RoadNoiseLnightStatus, x.RailNoiseStatus, x.RailNoiseLnightDb, x.RailNoiseLnightStatus, x.AirNoiseStatus, x.AirNoiseLnightDb, x.AirNoiseLnightStatus, x.ScoreCoveragePct, x.FamilyPrivacyAvailable, x.ScoreRuleVersion, x.ScoreNotesJson); }).ToList(); }
static async Task<Listing?> FindManualDuplicate(string canonicalUrl, string normalizedAddress, AppDbContext db, CancellationToken ct)
{
    var matches = await db.Listings.Where(x => x.CanonicalUrl == canonicalUrl || x.NormalizedAddress == normalizedAddress).ToListAsync(ct);
    var legacy = await db.Listings.Where(x => x.CanonicalUrl == null && x.SourceUrl != null).ToListAsync(ct);
    foreach (var candidate in legacy)
    {
        try { if (ManualListing.NormalizeUrl(candidate.SourceUrl!) == canonicalUrl && matches.All(x => x.Id != candidate.Id)) matches.Add(candidate); }
        catch (DomainException) { }
    }
    if (matches.Count > 1) throw new DomainException("Listing URL and address resolve to different existing listings.");
    return matches.SingleOrDefault();
}
static async Task<bool> HasConsensus(Guid listingId, AppDbContext db, CancellationToken ct)
{
    var members = await db.Members.AsNoTracking().ToListAsync(ct); var map = members.ToDictionary(x => x.Id, x => x.VotingIdentityId == Guid.Empty ? x.Id : x.VotingIdentityId);
    return ConsensusRules.HasConsensus(members.Where(x => x.IsActive).Select(x => map[x.Id]), await db.Votes.Where(x => x.ListingId == listingId).ToListAsync(ct), map);
}
static async Task<Guid> VotingIdentityFor(Guid memberId, AppDbContext db, CancellationToken ct) => await db.Members.Where(x => x.Id == memberId).Select(x => x.VotingIdentityId).SingleAsync(ct) is var id && id != Guid.Empty ? id : memberId;
static string DisplayName(Member member) => string.IsNullOrWhiteSpace(member.DisplayName) ? member.Email.Split('@')[0] : member.DisplayName;
static async Task<List<Member>?> ResolveCombinationMembers(CombineVotingIdentities request, AppDbContext db, CancellationToken ct)
{
    var requested = request.MemberIds.Append(request.PrimaryMemberId).Distinct().ToArray(); if (requested.Length < 2 || !requested.Contains(request.PrimaryMemberId)) return null;
    var selected = await db.Members.Where(x => requested.Contains(x.Id)).ToListAsync(ct); if (selected.Count != requested.Length) return null;
    var groupIds = selected.Select(x => x.VotingIdentityId == Guid.Empty ? x.Id : x.VotingIdentityId).Distinct().ToArray();
    var affected = await db.Members.Where(x => groupIds.Contains(x.VotingIdentityId) || (x.VotingIdentityId == Guid.Empty && groupIds.Contains(x.Id))).ToListAsync(ct);
    return affected.All(x => requested.Contains(x.Id)) ? affected : null;
}
static async Task<IResult> ChangeComment(Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, string? body, bool delete, CancellationToken ct)
{
    var listingId = await db.Comments.IgnoreQueryFilters().Where(x => x.Id == id).Select(x => (Guid?)x.ListingId).SingleOrDefaultAsync(ct);
    if (!listingId.HasValue) return Results.NotFound();
    await using var transaction = await db.Database.BeginTransactionAsync(ct);
    await LockListingMutation(listingId.Value, db, ct);
    if (!await CanAccessListing(listingId.Value, user, db, ct)) return Results.NotFound();
    var c = await db.Comments.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == id, ct);
    if (c is null) return Results.NotFound();
    try
    {
        if (delete) c.Delete(user.MemberId(), user.IsInRole(MemberRole.Owner.ToString()), clock.GetUtcNow()); else c.Edit(user.MemberId(), user.IsInRole(MemberRole.Owner.ToString()), body!, clock.GetUtcNow());
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        await hub.Clients.Group($"listing:{c.ListingId}").SendAsync("CommentChanged", c.Id, delete ? "deleted" : "edited", ct);
        return Results.Ok(new { c.Id, c.Body, c.IsDeleted, c.UpdatedAt });
    }
    catch (DomainException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); }
}
static BuildVersionDto RunningBuildVersion()
{
    var value = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    var release = value.Split('+', 2)[0];
    var version = release.Length >= 7 && release[..7].All(Uri.IsHexDigit) ? release[..7].ToLowerInvariant() : "dev";
    return new($"v{version}");
}
static string Csv(string value)
{
    if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = $"'{value}";
    return $"\"{value.Replace("\"", "\"\"")}\"";
}
static async Task LockListingMutation(Guid id, AppDbContext db, CancellationToken ct) => await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 0))", ct);
static async Task<bool> CanAccessListing(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) { var state = await db.Listings.Where(x => x.Id == id).Select(x => (ListingState?)x.State).SingleOrDefaultAsync(ct); return state.HasValue && (state != ListingState.Archived || user.IsInRole(MemberRole.Owner.ToString())); }
public static partial class Program { }
public static class ClaimsExtensions { public static Guid MemberId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()); }

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Data;
using HouseConsensus.Server.Listings;
using HouseConsensus.Server.Learning;
using HouseConsensus.Server.Hubs;
using HouseConsensus.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var debugAutoLogin = builder.Configuration.GetValue("Debug:AutoLogin", false);
var e2eTestAuth = builder.Configuration.GetValue("E2E:TestAuth", false);
var e2eSeedData = builder.Configuration.GetValue("E2E:SeedData", false);
DebugAutoLoginMiddleware.EnsureSafe(debugAutoLogin, builder.Environment.EnvironmentName);
DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(e2eTestAuth, debugAutoLogin, e2eSeedData, builder.Environment.EnvironmentName);
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database") ?? "Host=postgres;Database=house_consensus;Username=house_consensus;Password=house_consensus", n => n.MapEnum<MemberRole>("member_role").MapEnum<VoteChoice>("vote_choice").MapEnum<ListingState>("listing_state").MapEnum<ReasonTag>("reason_tag").MapEnum<OverrideAction>("override_action")));
var cloudflareAccess = AuthenticationSetup.Add(builder.Services, builder.Configuration, builder.Environment.IsProduction());
builder.Services.AddAuthorization(o => o.AddPolicy("owner", p => p.RequireClaim(ClaimTypes.Role, MemberRole.Owner.ToString())));
builder.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; o.ForwardLimit = 1; });
var magicRequestPermitLimit = builder.Configuration.GetValue("Auth:MagicRequestPermitLimit", 5);
var magicConsumePermitLimit = builder.Configuration.GetValue("Auth:MagicConsumePermitLimit", 20);
builder.Services.AddRateLimiter(o => { o.RejectionStatusCode = StatusCodes.Status429TooManyRequests; o.AddPolicy("magic-request", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = magicRequestPermitLimit, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 })); o.AddPolicy("magic-consume", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = magicConsumePermitLimit, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 })); });
builder.Services.AddSignalR(); builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();
builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddScoped<MagicLinkService>(); builder.Services.AddScoped<InviteService>(); builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddMemoryCache(options => options.SizeLimit = 128 * 1024 * 1024); builder.Services.AddHttpClient<ListingImageService>(client => { client.Timeout = TimeSpan.FromSeconds(15); client.DefaultRequestHeaders.UserAgent.ParseAdd("HouseConsensus/1.0"); });
if (builder.Environment.IsDevelopment() && e2eSeedData)
    builder.Services.AddScoped<IAiRuleGenerator, E2EAiRuleGenerator>();
else
    builder.Services.AddHttpClient<IAiRuleGenerator, OllamaAiRuleGenerator>(client => client.Timeout = TimeSpan.FromSeconds(120));
builder.Services.AddScoped<AiLearningService>();
var app = builder.Build();
app.UseForwardedHeaders();
if (app.Environment.IsProduction()) { app.UseHsts(); if (!cloudflareAccess.Enabled) app.UseHttpsRedirection(); }
app.UseExceptionHandler(e => e.Run(async c => { c.Response.StatusCode = 500; await c.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." }); }));
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.Use(async (context, next) => { var unsafeApiRequest = context.Request.Path.StartsWithSegments("/api") && (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)); if (unsafeApiRequest && context.Request.Headers["X-House-Consensus-CSRF"] != "1") { context.Response.StatusCode = StatusCodes.Status400BadRequest; await context.Response.WriteAsJsonAsync(new { error = "Missing same-origin request header." }); return; } await next(); });
app.UseAuthentication();
if (debugAutoLogin) app.UseMiddleware<DebugAutoLoginMiddleware>();
app.UseAuthorization();
app.MapHealthChecks("/health"); app.MapHub<ConsensusHub>("/hubs/consensus");

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

var members = app.MapGroup("/api/members").RequireAuthorization("owner");
members.MapGet("/", async (AppDbContext db, CancellationToken ct) => (await db.Members.OrderBy(x => x.Email).ToListAsync(ct)).Select(ToMemberDto));
members.MapPost("/invites", async (CreateInvite request, ClaimsPrincipal user, InviteService invites, CancellationToken ct) => { var email = MagicLinkService.Normalize(request.Email); if (!IsEmail(email)) return Results.BadRequest(new { error = "Invalid email." }); try { var invite = await invites.CreateAsync(email, user.MemberId(), ct); return Results.Created($"/api/members/invites/{invite.Id}", new { invite.Id, invite.Email, invite.ExpiresAt }); } catch (InviteConflictException) { return Results.Conflict(new { error = "Already a member." }); } });
members.MapPost("/{id:guid}/deactivate", async (Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, CancellationToken ct) => { if (id == user.MemberId()) return Results.BadRequest(new { error = "Owner cannot deactivate themselves." }); var member = await db.Members.FindAsync([id], ct); if (member is null) return Results.NotFound(); member.Deactivate(); await db.SaveChangesAsync(ct); await NotifyMembershipConsensus(id, false, db, hub, ct); return Results.NoContent(); });
members.MapPost("/{id:guid}/reactivate", async (Guid id, AppDbContext db, IHubContext<ConsensusHub> hub, CancellationToken ct) => { var member = await db.Members.FindAsync([id], ct); if (member is null) return Results.NotFound(); member.Reactivate(); await db.SaveChangesAsync(ct); await NotifyMembershipConsensus(id, true, db, hub, ct); return Results.NoContent(); });

var listings = app.MapGroup("/api/listings").RequireAuthorization();
listings.MapGet("/queue", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => (await ListingDtos(db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored).OrderByDescending(x => x.FamilyFitScore), db, ct)).Where(x => !x.Votes.Any(v => v.MemberId == user.MemberId() && v.Choice != VoteChoice.NotVoted)));
listings.MapGet("/browse", async (string? city, decimal? minPrice, decimal? maxPrice, AppDbContext db, CancellationToken ct) => { var q = db.Listings.Where(x => x.State != ListingState.Archived); if (!string.IsNullOrWhiteSpace(city)) q = q.Where(x => x.City != null && EF.Functions.ILike(x.City, $"%{city}%")); if (minPrice.HasValue) q = q.Where(x => x.Price >= minPrice); if (maxPrice.HasValue) q = q.Where(x => x.Price <= maxPrice); return await ListingDtos(q.OrderByDescending(x => x.FamilyFitScore), db, ct); });
listings.MapGet("/consensus", async (AppDbContext db, CancellationToken ct) => { var all = await ListingDtos(db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored), db, ct); return all.Where(x => x.Consensus); });
listings.MapGet("/my-votes", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var id = user.MemberId(); var latest = await db.Votes.Where(x => x.MemberId == id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct); return latest.GroupBy(x => x.ListingId).Select(g => g.First()).Where(x => x.Choice != VoteChoice.NotVoted).Select(v => ToVoteDto(v)); });
listings.MapGet("/{id:guid}/image", async (Guid id, ClaimsPrincipal user, AppDbContext db, ListingImageService images, CancellationToken ct) => { var listing = await db.Listings.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.State, x.PreviewImageUrl }).SingleOrDefaultAsync(ct); if (listing is null || (listing.State == ListingState.Archived && !user.IsInRole(MemberRole.Owner.ToString()))) return Results.NotFound(); var image = await images.GetAsync(id, listing.PreviewImageUrl, ct); return image is null ? Results.NotFound() : Results.Bytes(image.Bytes, image.ContentType); });
listings.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { var listing = await db.Listings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); if (listing is null) return Results.NotFound(); if (listing.State == ListingState.Archived && !user.IsInRole(MemberRole.Owner.ToString())) return Results.NotFound(); var all = await ListingDtos(db.Listings.Where(x => x.Id == id), db, ct); return Results.Ok(all[0]); });
listings.MapPost("/{id:guid}/votes", async (Guid id, CastVote request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => { if (request.Choice == VoteChoice.NotVoted) return Results.BadRequest(new { error = "Clearing votes is not supported." }); if (!await db.Listings.AnyAsync(x => x.Id == id && (x.State == ListingState.Active || x.State == ListingState.Restored), ct)) return Results.NotFound(); var vote = new Vote(id, user.MemberId(), request.Choice, request.Tags ?? [], request.Note, clock.GetUtcNow()); db.Votes.Add(vote); await db.SaveChangesAsync(ct); var consensus = await HasConsensus(id, db, ct); await hub.Clients.Group($"listing:{id}").SendAsync("VoteChanged", ToVoteDto(vote), consensus, ct); await hub.Clients.All.SendAsync("ConsensusChanged", id, consensus, ct); return Results.Ok(new { vote = ToVoteDto(vote), consensus }); });
listings.MapGet("/{id:guid}/votes/history", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => !await CanAccessListing(id, user, db, ct) ? Results.NotFound() : Results.Ok((await db.Votes.Where(x => x.ListingId == id).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).ToListAsync(ct)).Select(v => ToVoteDto(v))));
listings.MapPut("/{id:guid}/votes/note", async (Guid id, EditVoteNote request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => { var memberId = user.MemberId(); var vote = await db.Votes.Include(x => x.NoteRevisions).Where(x => x.ListingId == id && x.MemberId == memberId && x.Choice != VoteChoice.NotVoted).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct); if (vote is null) return Results.NotFound(); try { vote.EditNote(memberId, request.Note, clock.GetUtcNow()); await db.SaveChangesAsync(ct); var consensus = await HasConsensus(id, db, ct); await hub.Clients.Group($"listing:{id}").SendAsync("VoteChanged", ToVoteDto(vote), consensus, ct); return Results.Ok(ToVoteDto(vote)); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } });
listings.MapGet("/{id:guid}/comments", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => !await CanAccessListing(id, user, db, ct) ? Results.NotFound() : Results.Ok(await db.Comments.Where(x => x.ListingId == id).OrderBy(x => x.CreatedAt).Select(x => new { x.Id, x.AuthorId, x.Body, x.IsDeleted, x.CreatedAt, x.UpdatedAt }).ToListAsync(ct)));
listings.MapPost("/{id:guid}/comments", async (Guid id, AddComment request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => { if (!await CanAccessListing(id, user, db, ct)) return Results.NotFound(); try { var c = new Comment(id, user.MemberId(), request.Body, clock.GetUtcNow()); db.Comments.Add(c); await db.SaveChangesAsync(ct); await hub.Clients.Group($"listing:{id}").SendAsync("CommentChanged", c.Id, "created", ct); return Results.Created($"/api/comments/{c.Id}", new { c.Id, c.Body, c.AuthorId, c.CreatedAt }); } catch (DomainException ex) { return Results.BadRequest(new { error = ex.Message }); } });

var comments = app.MapGroup("/api/comments").RequireAuthorization();
comments.MapPut("/{id:guid}", async (Guid id, EditComment request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => await ChangeComment(id, user, db, hub, clock, request.Body, false, ct));
comments.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => await ChangeComment(id, user, db, hub, clock, null, true, ct));

var review = app.MapGroup("/api/review").RequireAuthorization("owner");
review.MapGet("/", async (AppDbContext db, CancellationToken ct) => await ListingDtos(db.Listings.Where(x => x.State == ListingState.AiRejected).OrderByDescending(x => x.AiConfidence), db, ct));
review.MapPost("/{id:guid}/override", async (Guid id, ApplyListingOverride request, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, CancellationToken ct) => { var l = await db.Listings.Include(x => x.Overrides).SingleOrDefaultAsync(x => x.Id == id, ct); if (l is null) return Results.NotFound(); l.ApplyOverride(request.Action, user.MemberId(), request.Reason, clock.GetUtcNow()); await db.SaveChangesAsync(ct); await hub.Clients.All.SendAsync("ListingStateChanged", id, l.State, ct); return Results.Ok(new { l.Id, l.State }); });

var feedback = app.MapGroup("/api/feedback").RequireAuthorization();
feedback.MapPost("/", async (SubmitFeedback request, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) => { if (string.IsNullOrWhiteSpace(request.Body) || request.Body.Length > 4000) return Results.BadRequest(new { error = "Feedback must be 1-4000 characters." }); if (request.ListingId.HasValue && !await db.Listings.AnyAsync(x => x.Id == request.ListingId.Value, ct)) return Results.BadRequest(new { error = "Unknown listing." }); var x = new Feedback { MemberId = user.MemberId(), ListingId = request.ListingId, Body = request.Body.Trim() }; db.Feedback.Add(x); await db.SaveChangesAsync(ct); return Results.Created($"/api/feedback/{x.Id}", x); });
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

static async Task Bootstrap(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Configuration.GetValue("Database:AutoMigrate", true))
    {
        await db.Database.MigrateAsync();
        await db.Database.OpenConnectionAsync();
        await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
    }
    var owner = MagicLinkService.Normalize(app.Configuration["INITIAL_OWNER_EMAIL"] ?? ""); if (!string.IsNullOrWhiteSpace(owner) && !await db.Members.AnyAsync()) { db.Members.Add(new Member { Email = owner, Role = MemberRole.Owner }); await db.SaveChangesAsync(); }
    if (!app.Environment.IsProduction() && app.Configuration.GetValue("E2E:SeedData", false)) await E2EDataSeeder.SeedAsync(db);
}
static bool IsEmail(string value) => System.Net.Mail.MailAddress.TryCreate(value, out var parsed) && parsed.Address == value.Trim();
static async Task SignIn(HttpContext c, Member m) { var claims = new[] { new Claim(ClaimTypes.NameIdentifier, m.Id.ToString()), new Claim(ClaimTypes.Email, m.Email), new Claim(ClaimTypes.Role, m.Role.ToString()) }; await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) }); }
static MemberDto ToMemberDto(Member m) => new(m.Id, m.Email, m.DisplayName, m.Language, m.Role, m.IsActive);
static VoteDto ToVoteDto(Vote v, string memberInitials = "") => new(v.Id, v.ListingId, v.MemberId, v.Choice, v.Tags, v.CreatedAt, v.Note, memberInitials);
static AiRuleProposalDto ToAiRuleProposalDto(AiRuleProposal x) { var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true }; var impact = JsonSerializer.Deserialize<AiRuleImpactDto>(x.ImpactPreviewJson, options) ?? new(0, 0, 0, 0, 0, []); var notes = JsonSerializer.Deserialize<List<AiRuleSourceNoteDto>>(x.SupportingNotesJson, options) ?? []; return new(x.Id, x.Version, x.VersionLabel, x.Summary, x.RuleJson, impact, notes, x.Status, x.IsActive, x.CreatedAt, x.ReviewedAt); }
static async Task<List<ListingDto>> ListingDtos(IQueryable<Listing> query, AppDbContext db, CancellationToken ct) { var items = await query.AsNoTracking().ToListAsync(ct); var ids = items.Select(x => x.Id).ToArray(); var votes = await db.Votes.AsNoTracking().Where(x => ids.Contains(x.ListingId)).ToListAsync(ct); var members = await db.Members.AsNoTracking().Select(x => new { x.Id, x.Email, x.DisplayName, x.IsActive }).ToListAsync(ct); var active = members.Where(x => x.IsActive).Select(x => x.Id).ToList(); var initials = members.ToDictionary(x => x.Id, x => AvatarInitials.From(x.DisplayName, x.Email)); return items.Select(x => { var vs = votes.Where(v => v.ListingId == x.Id).ToList(); return new ListingDto(x.Id, x.ExternalId, x.Address, x.City, x.Price, x.FamilyFitScore, x.State, x.AiAssessed, x.AiConfidence, x.AiEvidence, x.ModelVersion, x.RuleVersion, x.SourceUrl, ConsensusRules.HasConsensus(active, vs), ConsensusRules.LatestVotes(vs).Values.Select(v => ToVoteDto(v, initials.GetValueOrDefault(v.MemberId, ""))).ToArray(), x.PreviewImageUrl, x.LivingArea, x.LotArea, x.Rooms, x.YearBuilt, x.Bathrooms, x.Bedrooms, x.Floors, x.EnergyLabel, x.Quiet, x.BuildableHeadroom, x.GroundFloorBedroom, x.SeparateEntrance, x.SecondKitchen, x.PrivacyScore, x.FamilyPrivacyScore, x.KidsSpaceScore, x.GardenScore, x.SharedLivingScore, x.PracticalScore, x.FamilyPrivacyWeight, x.KidsSpaceWeight, x.GardenWeight, x.SharedLivingWeight, x.PracticalWeight, x.Latitude, x.Longitude, x.MonthlyExpense, x.DaysOnMarket, x.CommuteMinutes, x.BuildableStatus, x.Condition, x.GardenOrientation, x.MultigenFit, x.ImportedAt, x.PostalCode, x.Preferred, x.IsNew, x.FamilyUnits, x.CommuteJson, x.FirstSeenAt, x.RoadNoiseDb, x.RailNoiseDb, x.AirNoiseDb); }).ToList(); }
static async Task<bool> HasConsensus(Guid listingId, AppDbContext db, CancellationToken ct) => ConsensusRules.HasConsensus(await db.Members.Where(x => x.IsActive).Select(x => x.Id).ToListAsync(ct), await db.Votes.Where(x => x.ListingId == listingId).ToListAsync(ct));
static async Task<IResult> ChangeComment(Guid id, ClaimsPrincipal user, AppDbContext db, IHubContext<ConsensusHub> hub, TimeProvider clock, string? body, bool delete, CancellationToken ct) { var c = await db.Comments.Include(x => x.Revisions).SingleOrDefaultAsync(x => x.Id == id, ct); if (c is null || !await CanAccessListing(c.ListingId, user, db, ct)) return Results.NotFound(); try { if (delete) c.Delete(user.MemberId(), user.IsInRole(MemberRole.Owner.ToString()), clock.GetUtcNow()); else c.Edit(user.MemberId(), user.IsInRole(MemberRole.Owner.ToString()), body!, clock.GetUtcNow()); await db.SaveChangesAsync(ct); await hub.Clients.Group($"listing:{c.ListingId}").SendAsync("CommentChanged", c.Id, delete ? "deleted" : "edited", ct); return Results.Ok(new { c.Id, c.Body, c.IsDeleted, c.UpdatedAt }); } catch (DomainException ex) { return Results.Json(new { error = ex.Message }, statusCode: 403); } }
static string Csv(string value)
{
    if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = $"'{value}";
    return $"\"{value.Replace("\"", "\"\"")}\"";
}
static async Task NotifyMembershipConsensus(Guid memberId, bool active, AppDbContext db, IHubContext<ConsensusHub> hub, CancellationToken ct) { await hub.Clients.All.SendAsync("MembershipChanged", memberId, active, ct); var listingIds = await db.Listings.Where(x => x.State == ListingState.Active || x.State == ListingState.Restored).Select(x => x.Id).ToListAsync(ct); foreach (var listingId in listingIds) await hub.Clients.All.SendAsync("ConsensusChanged", listingId, await HasConsensus(listingId, db, ct), ct); }
static async Task<bool> CanAccessListing(Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) { var state = await db.Listings.Where(x => x.Id == id).Select(x => (ListingState?)x.State).SingleOrDefaultAsync(ct); return state.HasValue && (state != ListingState.Archived || user.IsInRole(MemberRole.Owner.ToString())); }
public static partial class Program { }
public static class ClaimsExtensions { public static Guid MemberId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException()); }

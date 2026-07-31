using System.Net;
using System.Net.Http.Json;
using HouseConsensus.Shared;

namespace HouseConsensus.Client.Services;

public sealed class ApiClient(HttpClient http)
{
    public async Task<T?> GetAsync<T>(string uri, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(uri, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return default;
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
    public Task<HttpResponseMessage> RequestMagicLink(string email, CancellationToken ct = default) => http.PostAsJsonAsync("api/auth/request", new RequestMagicLink(email), ct);
    public Task<HttpResponseMessage> Logout(CancellationToken ct = default) => http.PostAsync("api/auth/logout", null, ct);
    public async Task<MemberDto?> SetLanguage(string language, CancellationToken ct = default) => await SendAsync<MemberDto>(HttpMethod.Put, "api/auth/language", new UpdateLanguage(language), ct);
    public async Task<MemberDto?> UpdateProfile(string displayName, string avatarColor, CancellationToken ct = default) => await SendAsync<MemberDto>(HttpMethod.Put, "api/auth/profile", new UpdateProfile(displayName, avatarColor), ct);
    public async Task<VoteResult?> Vote(Guid id, VoteChoice choice, ReasonTag[] tags, string? note = null, CancellationToken ct = default) => await SendAsync<VoteResult>(HttpMethod.Post, $"api/listings/{id}/votes", new CastVote(choice, tags, note), ct);
    public async Task<HttpResponseMessage> EditVoteNote(Guid id, string? note, CancellationToken ct = default) => await RawAsync(HttpMethod.Put, $"api/listings/{id}/votes/note", new EditVoteNote(note), ct);
    public Task<HttpResponseMessage> AddComment(Guid id, string body, CancellationToken ct = default) => http.PostAsJsonAsync($"api/listings/{id}/comments", new AddComment(body), ct);
    public async Task<HttpResponseMessage> EditComment(Guid id, string body, CancellationToken ct = default) => await RawAsync(HttpMethod.Put, $"api/comments/{id}", new EditComment(body), ct);
    public Task<HttpResponseMessage> DeleteComment(Guid id, CancellationToken ct = default) => http.DeleteAsync($"api/comments/{id}", ct);
    public async Task<HttpResponseMessage> Override(Guid id, OverrideAction action, string? reason, CancellationToken ct = default) => await RawAsync(HttpMethod.Post, $"api/review/{id}/override", new ApplyListingOverride(action, reason), ct);
    public Task<HttpResponseMessage> SubmitFeedback(Guid? listingId, string body, CancellationToken ct = default) => http.PostAsJsonAsync("api/feedback", new SubmitFeedback(listingId, body), ct);
    public async Task<HttpResponseMessage> ReviewFeedback(Guid id, bool reviewed, CancellationToken ct = default) => await RawAsync(HttpMethod.Put, $"api/feedback/{id}/review", new ReviewFeedback(reviewed), ct);
    public Task<HttpResponseMessage> Invite(string email, CancellationToken ct = default) => http.PostAsJsonAsync("api/members/invites", new CreateInvite(email), ct);
    public Task<HttpResponseMessage> ChangeMember(Guid id, bool active, CancellationToken ct = default) => http.PostAsync($"api/members/{id}/{(active ? "reactivate" : "deactivate")}", null, ct);
    public async Task<AiRuleProposalDto?> GenerateAiRuleProposal(CancellationToken ct = default) => await SendAsync<AiRuleProposalDto>(HttpMethod.Post, "api/learning/proposals", new { }, ct);
    public async Task<HttpResponseMessage> ChangeAiRuleProposal(Guid id, string action, CancellationToken ct = default) => await RawAsync(HttpMethod.Post, $"api/learning/{id}/{action}", new { }, ct);

    private async Task<T?> SendAsync<T>(HttpMethod method, string uri, object body, CancellationToken ct)
    {
        using var response = await RawAsync(method, uri, body, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }
    private async Task<HttpResponseMessage> RawAsync(HttpMethod method, string uri, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        return await http.SendAsync(request, ct);
    }
    public static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct = default)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(string.IsNullOrWhiteSpace(detail) ? $"Request failed ({(int)response.StatusCode})." : detail, null, response.StatusCode);
    }
}

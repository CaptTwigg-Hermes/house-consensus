using HouseConsensus.Shared;
using Microsoft.JSInterop;

namespace HouseConsensus.Client.Services;

public sealed class AuthState(ApiClient api, IJSRuntime js)
{
    public MemberDto? User { get; private set; }
    public bool Ready { get; private set; }
    public bool IsOwner => User?.Role == MemberRole.Owner;
    public event Action? Changed;

    public async Task InitializeAsync()
    {
        if (Ready) return;
        try { User = await api.GetAsync<MemberDto>("api/auth/me"); }
        catch (HttpRequestException) { User = null; }
        if (User is not null) UiCulture.Apply(User.Language);
        Ready = true;
        Changed?.Invoke();
    }
    public async Task SetLanguageAsync(string language)
    {
        var normalized = UiCulture.Normalize(language);
        User = await api.SetLanguage(normalized) ?? User;
        UiCulture.Apply(normalized);
        await js.InvokeVoidAsync("hc.setCulture", normalized);
        Changed?.Invoke();
    }
    public async Task LogoutAsync()
    {
        using var response = await api.Logout();
        User = null;
        Changed?.Invoke();
    }
}

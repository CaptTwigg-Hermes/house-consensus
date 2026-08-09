using System.Globalization;
using HouseConsensus.Client;
using HouseConsensus.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => { var http = new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) }; http.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); return http; });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<ClientDiagnostics>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<LiveUpdates>();
builder.Services.AddSingleton<I18n>();
var host = builder.Build();
try
{
    var js = host.Services.GetRequiredService<IJSRuntime>();
    var browserLanguage = await js.InvokeAsync<string>("hc.browserLanguage");
    var culture = new CultureInfo(UiCulture.Normalize(browserLanguage));
    CultureInfo.DefaultThreadCurrentCulture = culture;
    CultureInfo.DefaultThreadCurrentUICulture = culture;
}
catch (Exception ex)
{
    await host.Services.GetRequiredService<ClientDiagnostics>().ReportAsync("startup", ex);
    throw;
}
await host.RunAsync();

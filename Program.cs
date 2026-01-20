using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using SnowDayPredictor;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Default HttpClient for the app
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Separate HttpClient for WeatherService (no BaseAddress restriction)
builder.Services.AddScoped<SnowDayPredictor.Services.WeatherService>(sp =>
{
    var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.Add("User-Agent", "SnowDayPredictor/1.0");
    return new SnowDayPredictor.Services.WeatherService(httpClient);
});

await builder.Build().RunAsync();

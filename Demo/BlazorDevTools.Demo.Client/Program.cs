using BlazorDevTools;
using BlazorDevTools.Demo.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddDemoServices();

// WebAssembly: the environment comes from the host builder, so pass it explicitly.
builder.Services.AddBlazorDevTools(options =>
{
    options.Enabled = builder.HostEnvironment.IsDevelopment();
    options.Ui.OpenByDefault = false;
});

await builder.Build().RunAsync();

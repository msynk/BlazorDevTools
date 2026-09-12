using BlazorDevTools;
using BlazorDevTools.Demo.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Match the server host: the demo deliberately registers a captive dependency and a cycle so the Services panel can
// diagnose them. WebAssembly validates registrations during Build by default, which would otherwise prevent the
// Interactive Auto client from starting before DevTools can show those registrations.
builder.UseDefaultServiceProvider(options => options.ValidateOnBuild = false);

builder.Services.AddDemoServices();

// WebAssembly: the environment comes from the host builder, so pass it explicitly.
builder.Services.AddBlazorDevTools(options =>
{
    options.Enabled = builder.HostEnvironment.IsDevelopment();
    options.Ui.OpenByDefault = false;
});

await builder.Build().RunAsync();

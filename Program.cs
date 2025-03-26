using MudBlazor.Services;
using MyApplicationTest.Components;
using MyApplicationTest.Services;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();
// Azure Service Bus Connection String
var serviceBusConnectionString = builder.Configuration.GetConnectionString("AzureServiceBusConnectionString");
var serviceBusQueueName = builder.Configuration.GetConnectionString("AzureServiceBusQueueName");
builder.Services.AddScoped(sp => new ServiceBusReceiverService(serviceBusConnectionString));
builder.Services.AddScoped(sp => new ServiceBusSenderService(serviceBusConnectionString));
// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

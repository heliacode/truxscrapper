using TruxScrapper;

var builder = WebApplication.CreateBuilder(args);

// Configurar servicios
builder.Services.AddSignalR();
builder.Services
    // .AddSingleton(_ => new SqliteConnection("Data Source=tracking.db"))
    // .AddSingleton<ITrackingRepository, TrackingRepository>()
    .AddSingleton<OrderTrackerHub>();
//builder.Services.AddHostedService(s => new OrderTrackerBackgroundService(s.GetRequiredService<ITrackingRepository>(), s.GetRequiredService<OrderTrackerHub>()));



var app = builder.Build();

app.UseHttpsRedirection();

// Serve the index.html file as the default entry page
app.UseDefaultFiles();
app.UseStaticFiles();

// Configurar SignalR hub
app.MapHub<OrderTrackerHub>("/ordertracker");

// Initialize the database
// await app.Services.GetRequiredService<ITrackingRepository>().InitializeDatabaseAsync();

app.Run();

namespace TruxScrapper.Services.Providers;

public interface ITrackingScraper
{
    Task<List<(string Date, string Status)>> ScrapeStatusHistoryAsync(string trackingNumber);
}

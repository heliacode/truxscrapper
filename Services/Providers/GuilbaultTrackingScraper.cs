using Microsoft.Playwright;
using TruxScrapper.Services.Providers;

namespace TruxScrapper.Services.Providers;

public class GuilbaultTrackingScraper : ITrackingScraper
{
    private readonly ILogger<GuilbaultTrackingScraper> _logger;

    public GuilbaultTrackingScraper(ILogger<GuilbaultTrackingScraper> logger)
    {
        _logger = logger;
    }

    public async Task<List<(string Date, string Status)>> ScrapeStatusHistoryAsync(string trackingNumber)
    {
        _logger.LogInformation("Launching Playwright...");
        using var playwright = await Playwright.CreateAsync();

        _logger.LogInformation("Launching browser...");
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();

        _logger.LogInformation("Navigating to Guilbault tracking page...");
        await page.GotoAsync("https://grguweb.tmwcloud.com/trace/external.msw");

        _logger.LogInformation("Locating tracking input field...");
        var input = await page.QuerySelectorAsync("//input[@type='hidden' and @value='~PTLORDER']/following-sibling::input[@name='search_value[]']");
        if (input is null)
        {
            _logger.LogError("Tracking input field not found.");
            throw new Exception("Tracking input field not found.");
        }

        _logger.LogInformation("Filling tracking number...");
        await input.FillAsync(trackingNumber);

        _logger.LogInformation("Listening for new tab...");
        IPage newPage = null;
        context.Page += (_, e) => newPage = e;

        _logger.LogInformation("Clicking submit...");
        await page.ClickAsync("input[type='button'][value='Submit']");

        // Wait for new tab to open
        for (int i = 0; i < 50 && newPage == null; i++)
        {
            await Task.Delay(100);
        }

        if (newPage == null)
        {
            throw new Exception("New tab did not open after submit.");
        }

        await newPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        _logger.LogInformation("New tab loaded. URL: " + newPage.Url);

        // Wait for actual data rows to appear
        _logger.LogInformation("Waiting for table rows to appear...");
        await newPage.WaitForSelectorAsync("div.k-grid-content tbody tr", new()
        {
            Timeout = 15000,
            State = WaitForSelectorState.Attached
        });

        // Optional soft retry loop to be 100% sure
        var rows = new List<IElementHandle>();
        for (int i = 0; i < 10; i++)
        {
            rows = (await newPage.QuerySelectorAllAsync("div.k-grid-content tbody tr")).ToList();
            if (rows.Count > 0) break;

            _logger.LogInformation("No rows yet... retrying...");
            await Task.Delay(500);
        }

        _logger.LogInformation($"Found {rows.Count} rows.");

        var results = new List<(string Date, string Status)>();

        foreach (var row in rows)
        {
            var cells = await row.QuerySelectorAllAsync("td");
            if (cells.Count >= 2)
            {
                var dateText = await cells[0].InnerTextAsync();
                var statusCode = await cells[1].InnerTextAsync();

                _logger.LogInformation($"Row: {dateText} | {statusCode}");

                results.Add((dateText.Trim(), statusCode.Trim()));
            }
        }

        return results;
    }
}

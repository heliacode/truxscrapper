using Microsoft.Playwright;

using System.Collections.Concurrent;

namespace TruxScrapper;

public class OrderTrackerService()
{
    readonly ConcurrentDictionary<string, CancellationTokenSource> cancelByClients = [];

    ~OrderTrackerService()
    {
        var cancelByClientsKey = cancelByClients.Keys.ToArray().AsSpan();

        foreach (var clientId in cancelByClientsKey)
        {
            if (cancelByClients.TryRemove(clientId, out var cancellor))
            {
                try
                {
                    cancellor.Cancel();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Error removing client {clientId}): {ex.Message}");
                }
            }
        }
    }

    public async Task UpdateConnectionIdAsync(
        string clientName,
        string[] trackingNumbers,
        Func<string, List<StatusHistory>, Task> pipe,
        CancellationToken requestCancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            AppLogger.Warn("Client name cannot be null or empty.");
            return;
        }

        if (trackingNumbers.Length == 0)
        {
            AppLogger.Warn("No tracking numbers found for any clients.");
            return;
        }

        AppLogger.Info("Launching browser...");

        try
        {
            List<(string name, Scrapper scrapper)> providers = [("Guibault", GetFromGuibaultAsync), ("Minimax", GetFromMinimaxAsync)];

            await Task
                .WhenAll(trackingNumbers.Select(number =>
                {
                    TaskCompletionSource logsCollectorWinner = new();
                    requestCancellationToken.Register(() => logsCollectorWinner.TrySetCanceled());
                    ConcurrentBag<CancellationTokenSource> cancellers = [];

                    providers.ForEach((provider) =>
                    {
                        CancellationTokenSource cancellationTokenSource = new();
                        cancellers.Add(cancellationTokenSource);

                        try
                        {
                            AppLogger.Info(provider.name, number, "Starting scrapper...");

                            provider.scrapper(clientName, number, cancellationTokenSource.Token).ContinueWith(async providerResult =>
                            {
                                if (providerResult.Result is { } scrapperTask)
                                {
                                    foreach (var canceller in cancellers.Except([cancellationTokenSource]))
                                    {
                                        canceller.Cancel();
                                    }

                                    if (await scrapperTask() is { } logs)
                                    {
                                        await pipe(number, logs);
                                    }
                                }
                                logsCollectorWinner.TrySetResult();
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error(provider.name, number, $"ScraperError: {ex}");
                        }
                    });

                    return logsCollectorWinner.Task;
                }))
                .WaitAsync(requestCancellationToken);

            //			TaskCompletionSource updateCompletion = new();
            //			ConcurrentDictionary<string, List<CancellationTokenSource>> cancellers = [];
            //			int total = trackingNumbers.Length * providers.Count;
            //
            //			/* Race condition implementation */
            //
            //			foreach (var (cancellor, number, scrapperTask) in
            //							from number in trackingNumbers
            //							from source in providers
            //							let cancellor = new CancellationTokenSource()
            //							select (cancellor, number, source.scrapper(clientName, number, cancellor.Token)))
            //			{
            //				if (cancellers.TryGetValue(number, out var numberCancellers))
            //				{
            //					numberCancellers.Add(cancellor);
            //				}
            //				else
            //				{
            //					numberCancellers = cancellers[number] = [cancellor];
            //				}
            //				_ = scrapperTask.ContinueWith(t =>
            //				{
            //					if (t.Exception is null && t.Result is { } populator)
            //					{
            //						numberCancellers.Remove(cancellor);
            //						numberCancellers.ForEach(c =>
            //						{
            //							c.Cancel();
            //						});
            //
            //						populator().ContinueWith(p =>
            //						{
            //							(number, p.Result).Dump();
            //							if (Interlocked.Decrement(ref total) is 0)
            //							{
            //								updateCompletion.SetResult();
            //							}
            //						});
            //					}
            //					else
            //					{
            //						if (Interlocked.Decrement(ref total) is 0)
            //						{
            //							updateCompletion.SetResult();
            //						}
            //					}
            //				});
            //			}

            //await updateCompletion.Task;

        }
        catch (Exception ex)
        {
            AppLogger.Error($"General error: {ex}");
        }
    }

    public static async Task<Func<Task<List<StatusHistory>>>?> GetFromMinimaxAsync(
        string clientName,
        string trackingNumber,
        CancellationToken cancellationToken)
    {
        var disposed = false;
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Timeout = 60000, Headless = true });
        var context = await browser.NewContextAsync(new() { JavaScriptEnabled = true });
        var source = clientName + "@Minimax";
        var page = await context.NewPageAsync();

        cancellationToken.Register(async () =>
        {
            AppLogger.Warn(source, trackingNumber, "Cancelling because of found in other provider");
            await DisposeAsync();
        });

        try
        {
            AppLogger.Info(source, trackingNumber, "Opening page...");
            await page.GotoAsync("https://minimax.tracking.dtms.ca", new() { Timeout = 60000 });

            AppLogger.Info(source, trackingNumber, $"Filling with tracking number...");
            if (await page.WaitForSelectorAsync("input#mat-input-2") is not { } inputField)
            {
                AppLogger.Error(source, trackingNumber, "Tracking input field not found.");
                await DisposeAsync();
                return null;
            }

            await inputField.FillAsync(trackingNumber);

            AppLogger.Info(source, trackingNumber, "Submitting...");
            await page.ClickAsync("button[mattooltip=\"Click to search\"]");

            AppLogger.Info(source, trackingNumber, "Waiting for status logs...");
            if (await page.WaitForSelectorAsync("tbody[role=\"presentation\"] tr:not(:first-child):not(.dx-freespace-row)") is not { } row)
            {
                AppLogger.Warn(source, trackingNumber, "Status logs not available!");
                await DisposeAsync();
                return null;
            }

            return Populate;

            async Task<List<StatusHistory>> Populate()
            {
                List<StatusHistory> results = [];
                try
                {
                    while (row is { })
                    {
                        var cells = await row.QuerySelectorAllAsync("td");
                        var date = await cells[1].InnerTextAsync();

                        if ((date?.Trim() ?? "") is "") break;

                        var time = await cells[2].InnerTextAsync();
                        var status = await cells[7].InnerTextAsync();
                        var location = await cells[9].InnerTextAsync();
                        results.Add(new($"{date} {time}", status, location));
                        AppLogger.Info(source, trackingNumber, $"Row: {date} {time} | {status}");
                        row = (await row.EvaluateHandleAsync("n => n.nextElementSibling"))?.AsElement();
                    }

                    AppLogger.Warn(source, trackingNumber, results.Count == 0
                        ? "Table loaded, but no status rows found."
                        : $"Successfully extracted {results.Count} status entries.");
                }
                catch (Exception ex)
                {
                    AppLogger.Error(source, trackingNumber, $"ScrapperError @ getting rows: {ex}");
                }
                finally
                {
                    await DisposeAsync();
                }

                return results;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(source, trackingNumber, $"ScraperError @ Scanning page: {ex}");
            await DisposeAsync();
            return null;
        }

        async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await page.CloseAsync();
            await context.CloseAsync();
            await context.DisposeAsync();
            await browser.CloseAsync();
            await browser.DisposeAsync();
            playwright.Dispose();
        }
    }

    public static async Task<Func<Task<List<StatusHistory>>>?> GetFromGuibaultAsync(string clientName, string trackingNumber, CancellationToken cancellationToken = default)
    {
        var source = clientName + "@Guibaiult";
        var disposed = false;

        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Timeout = 60000, Headless = true });
        var context = await browser.NewContextAsync(new() { JavaScriptEnabled = true });
        var page = await context.NewPageAsync();

        cancellationToken.Register(async () =>
        {
            AppLogger.Info(source, trackingNumber, "Cancelling because it was already resolved by other provider");
            await DisposeAsync();
        });

        try
        {
            AppLogger.Info(source, trackingNumber, "Opening page...");
            await page.GotoAsync("https://grguweb.tmwcloud.com/trace/external.msw", new() { Timeout = 60000 });

            AppLogger.Info(source, trackingNumber, "Locating tracking input field...");
            if (await page.QuerySelectorAsync("//input[@type='hidden' and @value='~PTLORDER']/following-sibling::input[@name='search_value[]']") is not { } input)
            {
                AppLogger.Info(source, trackingNumber, "Tracking input field not found.");
                await ClosePageAsync(page);
                await DisposeAsync();
                return null;
            }

            AppLogger.Info(source, trackingNumber, "Filling tracking number...");
            await input.FillAsync(trackingNumber);

            TaskCompletionSource<Func<Task<List<StatusHistory>>>?> resolverCompleted = new();

            context.Page += async (_, newPage) =>
            {
                try
                {
                    await newPage.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
                    AppLogger.Info(source, trackingNumber, "New tab loaded. URL: " + newPage.Url);

                    // Wait for actual data rows to appear
                    AppLogger.Info(source, trackingNumber, "Waiting for status logs...");
                    if (await newPage.WaitForSelectorAsync("div.k-grid-content tbody tr", new() { State = WaitForSelectorState.Attached }) is not { } row
                        //|| (await row.EvaluateAsync("n => n.closest('body')?.querySelector('#billDetailsOutput h2 span')?.innerText?.trim()")) is not {} jsonValue
                        //|| jsonValue.GetString() != trackingNumber
                        )
                    {
                        AppLogger.Warn(source, trackingNumber, "Status logs not available!");
                        await ClosePageAsync(newPage);
                        await DisposeAsync();
                        Resolve();
                        return;
                    }

                    resolverCompleted.SetResult(Populate);

                    async Task<List<StatusHistory>> Populate()
                    {
                        List<StatusHistory> results = [];
                        try
                        {
                            while (row is { })
                            {
                                var cells = await row.QuerySelectorAllAsync("td");
                                if (cells.Count >= 2)
                                {
                                    var dateText = await cells[0].InnerTextAsync();
                                    var statusCode = await cells[1].InnerTextAsync();

                                    AppLogger.Info(source, trackingNumber, $"Row: {dateText} | {statusCode}");

                                    results.Add(new(dateText.Trim(), statusCode.Trim(), ""));
                                }
                                row = (await row.EvaluateHandleAsync("n => n.nextElementSibling")).AsElement();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error(source, trackingNumber, $"Popup ScraperError @ Scanning page: {ex}");
                        }
                        finally
                        {
                            await newPage.CloseAsync();
                            await DisposeAsync();
                        }

                        return results;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error(source, trackingNumber, $"Popup ScraperError @ Updates error: {ex.Message}");

                    await newPage.CloseAsync();

                    if (!resolverCompleted.Task.IsCompleted) resolverCompleted.SetResult(null);

                    await DisposeAsync();
                }
            };

            AppLogger.Info(source, trackingNumber, "Submitting...");
            await page.ClickAsync("input[type='button'][value='Submit']");
            await ClosePageAsync(page);

            return await resolverCompleted.Task;

            void Resolve(Func<Task<List<StatusHistory>>>? resolver = null)
            {
                if (!resolverCompleted.Task.IsCompleted) resolverCompleted.TrySetResult(resolver);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(source, trackingNumber, $"ScraperError @ Scanning page: {ex}");
            await ClosePageAsync(page);
            await DisposeAsync();
            return null;
        }

        async ValueTask ClosePageAsync(IPage page)
        {
            if (!page.IsClosed) await page.CloseAsync();
        }

        async Task DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await context.CloseAsync();
            await context.DisposeAsync();
            await browser.CloseAsync();
            await browser.DisposeAsync();
        }
    }
}


delegate Task<Func<Task<List<StatusHistory>>>?> Scrapper(
    string clientName,
    string trackingNumber,
    CancellationToken cancellationToken);
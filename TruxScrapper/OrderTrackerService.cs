using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Microsoft.Playwright;

namespace TruxScrapper;

public class OrderTrackerService()
{
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

        TaskCompletionSource updateCompletion = new(requestCancellationToken);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Timeout = 60000, Headless = true });
        var context = await browser.NewContextAsync(new() { JavaScriptEnabled = true });

        try
        {

            ConcurrentDictionary<string, List<CancellationTokenSource>> cancellers = [];
            (string name, Scrapper scrapper)[] providers = [("Minimax", GetFromMinimaxAsync), ("Guibault", GetFromGuibaultAsync)];
            int total = trackingNumbers.Length * providers.Length;

            /* Race condition implementation */

            foreach (var (numberCancellator, number, scrapperTask) in
                            from number in trackingNumbers
                            from source in providers
                            let cancellor = new CancellationTokenSource()
                            select (cancellor, number, source.scrapper(playwright, context, clientName, number, cancellor.Token)))
            {
                if (cancellers.TryGetValue(number, out var numberCancellations))
                {
                    numberCancellations.Add(numberCancellator);
                }
                else
                {
                    numberCancellations = cancellers[number] = [numberCancellator];
                }

                _ = scrapperTask.ContinueWith(resolvedScrapper =>
                {
                    if (resolvedScrapper.Exception is null && resolvedScrapper.Result is { } fetchLogs)
                    {
                        numberCancellations.Remove(numberCancellator);
                        numberCancellations.ForEach(remainingNumberCancellations =>
                        {
                            remainingNumberCancellations.Cancel();
                        });

                        fetchLogs().ContinueWith(async resolvedLogs =>
                        {
                            await pipe(number, resolvedLogs.Result);

                            if (Interlocked.Decrement(ref total) is 0)
                            {
                                updateCompletion.SetResult();
                            }
                        });
                    }
                    else
                    {
                        if (Interlocked.Decrement(ref total) is 0)
                        {
                            updateCompletion.SetResult();
                        }
                    }
                });
            }

            await updateCompletion.Task;

        }
        catch (Exception ex)
        {
            AppLogger.Error($"General error: {ex}");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    public static async Task<Func<Task<List<StatusHistory>>>?> GetFromMinimaxAsync(IPlaywright playwrite, IBrowserContext context, string clientName, string trackingNumber, CancellationToken cancellationToken)
    {
        var source = clientName + "@Minimax";
        var page = await context.NewPageAsync();
        cancellationToken.Register(async () => await page.CloseAsync());

        try
        {
            AppLogger.Info(source, trackingNumber, "Opening page...");
            await page.GotoAsync("https://minimax.tracking.dtms.ca");

            AppLogger.Info(source, trackingNumber, $"Filling with tracking number...");
            if (await page.WaitForSelectorAsync("input#mat-input-2") is not { } inputField)
            {
                AppLogger.Error(source, trackingNumber, "Tracking input field not found.");
                await page.CloseAsync();
                return null;
            }

            await inputField.FillAsync(trackingNumber);

            AppLogger.Info(source, trackingNumber, "Submitting...");
            await page.ClickAsync("button[mattooltip=\"Click to search\"]");

            AppLogger.Info(source, trackingNumber, "Waiting for status logs...");
            if (await page.WaitForSelectorAsync("tbody[role=\"presentation\"]") is not { } tbody)
            {
                AppLogger.Warn(source, trackingNumber, "Status logs not available!");
                await page.CloseAsync();
                return null;
            }
            // Query rows inside the correct section
            var statusRows = await page.QuerySelectorAllAsync("tbody[role=\"presentation\"] tr:not(:first-child):not(.dx-freespace-row)");

            if (statusRows.Count == 0) return null;

            return Populate;

            async Task<List<StatusHistory>> Populate()
            {
                List<StatusHistory> results = [];
                try
                {
                    foreach (var row in statusRows)
                    {
                        var cells = await row.QuerySelectorAllAsync("td");
                        var date = await cells[1].InnerTextAsync();
                        var time = await cells[2].InnerTextAsync();
                        var status = await cells[7].InnerTextAsync();
                        var location = await cells[9].InnerTextAsync();
                        results.Add(new($"{date} {time}", status, location));
                        AppLogger.Info(source, trackingNumber, $"Row: {date} {time} | {status}");
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
                    await page.CloseAsync();
                }

                return results;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error(source, trackingNumber, $"ScraperError @ Scanning page: {ex}");
            return null;
        }
    }

    public static async Task<Func<Task<List<StatusHistory>>>?> GetFromGuibaultAsync(IPlaywright playwright, IBrowserContext _, string clientName, string trackingNumber, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<Func<Task<List<StatusHistory>>>?> resolverCompleted = new();
        var source = clientName + "@Guibaiult";
        var disposed = false;

        AppLogger.Info(source, trackingNumber, "Guibault provider requires a new browser per tracking number...");
        var browser = await playwright.Chromium.LaunchAsync(new() { Timeout = 60000, Headless = true });
        var context = await browser.NewContextAsync(new() { JavaScriptEnabled = true });

        var page = await context.NewPageAsync();
        cancellationToken.Register(async () =>
        {
            await DisposeAsync();
        });
        try
        {
            AppLogger.Info(source, trackingNumber, "Opening page...");
            await page.GotoAsync("https://grguweb.tmwcloud.com/trace/external.msw");

            AppLogger.Info(source, trackingNumber, "Locating tracking input field...");
            if (await page.QuerySelectorAsync("//input[@type='hidden' and @value='~PTLORDER']/following-sibling::input[@name='search_value[]']") is not { } input)
            {
                AppLogger.Info(source, trackingNumber, "Tracking input field not found.");
                await DisposeAsync();
                return null;
            }

            AppLogger.Info(source, trackingNumber, "Filling tracking number...");
            await input.FillAsync(trackingNumber);

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
                        await newPage.CloseAsync();
                        resolverCompleted.SetResult(null);
                        await DisposeAsync();
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
            await page.CloseAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error(source, trackingNumber, $"ScraperError @ Scanning page: {ex}");

            if (!resolverCompleted.Task.IsCompleted) resolverCompleted.SetResult(null);

            await DisposeAsync();
        }

        return await resolverCompleted.Task;

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

delegate Task<Func<Task<List<StatusHistory>>>?> Scrapper(IPlaywright playwrigt, IBrowserContext context, string clientName, string trackingNumber, CancellationToken cancellationToken = default);

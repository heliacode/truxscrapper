using Microsoft.Playwright;

using System.Collections.Concurrent;
using System.Net.Mime;

namespace TruxScrapper;

public delegate Task PipeNotifier(string trackingNumber, List<StatusHistory> logs);
public class OrderTrackerService()
{
	public static async Task UpdateConnectionIdAsync(
		string clientName,
		string[] trackingNumbers,
		PipeNotifier pipe)
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
			
			List<(string name, Scrapper scrapper)> providers = [("Minimax", GetFromMinimaxAsync), ("Guibault", GetFromGuibaultAsync)];
			using var playwright = await Playwright.CreateAsync();

			ConcurrentDictionary<string, List<CancellationTokenSource>> cancellers = [];
			int total = trackingNumbers.Length * providers.Count;

			/* Race condition implementation */

			List<Task> numberResolvers = [];

			foreach (var number in trackingNumbers)
			{
				TaskCompletionSource logsResolver = new();

				numberResolvers.Add(logsResolver.Task);

				int count = providers.Count;

				_ = playwright.Chromium
					.LaunchAsync(new() { Timeout = 60000, Headless = true })
					   .ContinueWith(async browserTask =>
					{
						foreach (var (name, scrapper) in providers)
						{
							var browser = browserTask.Result;
							var context = await browser.NewContextAsync(new() { JavaScriptEnabled = true });

							_ = scrapper(context, clientName, number, default).ContinueWith(async t =>
								{
									if (t.Result is { } populator && await populator() is { } logs)
									{
										await pipe(number, [.. logs.OrderByDescending(r => r.Timestamp)]);
										logsResolver.SetResult();
									}
									if (--count == 0 && !logsResolver.Task.IsCompleted)
									{
										await pipe(number, []);
										logsResolver.SetResult();
									}
								});
						}
					});
			}

			await Task.WhenAll(numberResolvers);
		}
		catch (Exception ex)
		{
			AppLogger.Error($"General error: {ex}");
		}
	}

	public static async Task<Func<Task<List<StatusHistory>>>?> GetFromMinimaxAsync(
		IBrowserContext context,
		string clientName,
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var disposed = false;
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

						if (await cells[1].InnerTextAsync() is not { Length: > 0 } date) break;

						var time = await cells[2].InnerTextAsync();

						// Parse date and time from input format MM/dd/yyyy hh:mm
						var dateTime = DateTime.ParseExact(
							$"{date} {time}",
							"MM/dd/yyyy HH:mm",
							System.Globalization.CultureInfo.InvariantCulture,
							System.Globalization.DateTimeStyles.None);


						var status = await cells[7].InnerTextAsync();

						if((await cells[9].InnerTextAsync()).Split(':') is not [var location, var company])
						{
							continue;
						}

						results.Add(new(dateTime, status, location, company));
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
		}
	}

	public static async Task<Func<Task<List<StatusHistory>>>?> GetFromGuibaultAsync(
		IBrowserContext context,
		string clientName,
		string trackingNumber,
		CancellationToken cancellationToken = default)
	{
		var source = clientName + "@Guibaiult";

		var page = await context.NewPageAsync();

		cancellationToken.Register(async () =>
		{
			AppLogger.Info(source, trackingNumber, "Cancelling because it was already resolved by other provider");
			await page.CloseAsync();
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

									var dateTime = 	DateTime.ParseExact(
														dateText.Trim(),
														"yyyy-MM-dd hh:mm tt",
														System.Globalization.CultureInfo.InvariantCulture,
														System.Globalization.DateTimeStyles.None);

									var statusCode = await cells[1].InnerTextAsync();

									AppLogger.Info(source, trackingNumber, $"Row: {dateText} | {statusCode}");

									results.Add(new(dateTime, statusCode.Trim()));
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
						}

						return results;
					}
				}
				catch (Exception ex)
				{
					AppLogger.Error(source, trackingNumber, $"Popup ScraperError @ Updates error: {ex.Message}");

					await newPage.CloseAsync();

					if (!resolverCompleted.Task.IsCompleted) resolverCompleted.SetResult(null);
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
			return null;
		}

		async ValueTask ClosePageAsync(IPage page)
		{
			if (!page.IsClosed) await page.CloseAsync();
		}
	}
}

delegate Task<Func<Task<List<StatusHistory>>>?> Scrapper(
    IBrowserContext context,
    string clientName,
    string trackingNumber,
    CancellationToken cancellationToken);
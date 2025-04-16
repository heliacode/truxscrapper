using Microsoft.AspNetCore.Mvc;
using TruxScrapper.Models;
using TruxScrapper.Services.Providers;

namespace TruxScrapper.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScrapperController : ControllerBase
{
    private readonly ITrackingScraper _scraper;
public ScrapperController(ITrackingScraper scraper)
{
    _scraper = scraper;
}
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            status = "TruxScrapper is up and running.",
            time = DateTime.UtcNow
        });
    }

[HttpPost("track-order")]
public async Task<IActionResult> TrackOrder([FromBody] TrackOrderRequest request)
{
    var orderId = request.OrderId?.Trim();

    if (string.IsNullOrEmpty(orderId) || orderId.Equals("string", StringComparison.OrdinalIgnoreCase))
    {
        return BadRequest(new { error = "A valid OrderId is required." });
    }

    try
    {
        var history = await _scraper.ScrapeStatusHistoryAsync(orderId);

        return Ok(new
        {
            orderId,
            statusHistory = history.Select(h => new { date = h.Date, status = h.Status }).ToList()
        });
    }
    catch (Exception ex)
    {
        return StatusCode(500, new { error = "Scraping failed.", details = ex.Message });
    }
}

}


namespace TruxScrapper;


public record StatusHistory(DateTime Timestamp, string Status, string Location = "", string Company = "");

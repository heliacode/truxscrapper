
namespace TruxScrapper;


public record StatusHistory(DateTime Timestamp, string Status, bool IsCompleted, string Location = "", string Company = "");

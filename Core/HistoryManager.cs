using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TSN_MULTI_API.Core
{
    public class OrderRecord
    {
        public long OrderId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string Snils { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string StatusName { get; set; } = "Зарегистрировано на портале";
        public string StateOrgStatusCode { get; set; } = string.Empty;
        public long CurrentStatusHistoryId { get; set; }
        public bool HasResult { get; set; }
        public List<ResultFileInfo> ResultFiles { get; set; } = new();
        public bool IsCompleted { get; set; }
    }

    public class ResultFileInfo
    {
        public string FileName { get; set; } = string.Empty;
        public string Link { get; set; } = string.Empty;
    }

    public static class HistoryManager
    {
        private static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orders_history.json");

        public static List<OrderRecord> LoadHistory()
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<OrderRecord>();
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<List<OrderRecord>>(json) ?? new List<OrderRecord>();
            }
            catch
            {
                return new List<OrderRecord>();
            }
        }

        public static void SaveHistory(List<OrderRecord> records)
        {
            try
            {
                string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public static void AddOrUpdate(OrderRecord record)
        {
            var list = LoadHistory();
            list.RemoveAll(x => x.OrderId == record.OrderId);
            list.Insert(0, record);
            SaveHistory(list);
        }
    }
}

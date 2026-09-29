using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TSN_MULTI_API.Core
{
    public class AddressBookEntry
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string LastName { get; set; } = string.Empty;
        public string FirstName { get; set; } = string.Empty;
        public string Patronymic { get; set; } = string.Empty;
        public string Snils { get; set; } = string.Empty;

        public string FullName =>
            string.Join(" ", new[] { LastName, FirstName, Patronymic }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static class AddressBookManager
    {
        private static readonly string BookPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "address_book.json");

        public static List<AddressBookEntry> Load()
        {
            if (!File.Exists(BookPath))
                return new List<AddressBookEntry>();

            try
            {
                var json = File.ReadAllText(BookPath);
                return JsonSerializer.Deserialize<List<AddressBookEntry>>(json) ?? new List<AddressBookEntry>();
            }
            catch
            {
                return new List<AddressBookEntry>();
            }
        }

        public static void Save(IEnumerable<AddressBookEntry> entries)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(BookPath, JsonSerializer.Serialize(entries.ToList(), options));
        }
    }

    public static class SnilsFormatter
    {
        public static string Format(string raw)
        {
            string text = new string((raw ?? string.Empty).Where(char.IsDigit).ToArray());
            if (text.Length > 11)
                text = text[..11];

            var formatted = string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                if (i == 3 || i == 6)
                    formatted += "-";
                else if (i == 9)
                    formatted += " ";
                formatted += text[i];
            }

            return formatted;
        }

        public static bool IsComplete(string formatted) =>
            new string((formatted ?? string.Empty).Where(char.IsDigit).ToArray()).Length == 11;
    }
}

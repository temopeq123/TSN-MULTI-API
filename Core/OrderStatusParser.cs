using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace TSN_MULTI_API.Core
{
    public static class OrderStatusParser
    {
        public static void ApplyFromResponse(OrderRecord order, string responseJson)
        {
            using var doc = JsonDocument.Parse(responseJson);
            JsonElement root = doc.RootElement;

            if (TryGetPropertyIgnoreCase(root, "order", out JsonElement orderProp))
            {
                if (orderProp.ValueKind == JsonValueKind.String)
                {
                    string? orderStr = orderProp.GetString();
                    if (!string.IsNullOrWhiteSpace(orderStr))
                    {
                        using var orderDoc = JsonDocument.Parse(orderStr);
                        ApplyOrderDetails(order, orderDoc.RootElement);
                        return;
                    }
                }

                if (orderProp.ValueKind == JsonValueKind.Object)
                {
                    ApplyOrderDetails(order, orderProp);
                    return;
                }
            }

            ApplyOrderDetails(order, root);
        }

        private static void ApplyOrderDetails(OrderRecord order, JsonElement orderElement)
        {
            if (TryGetString(orderElement, "orderStatusName", out string? statusName) ||
                TryGetString(orderElement, "statusName", out statusName) ||
                TryGetString(orderElement, "orderStatus", out statusName))
            {
                order.StatusName = statusName ?? order.StatusName;
            }

            if (TryGetString(orderElement, "stateOrgStatusCode", out string? stateCode))
                order.StateOrgStatusCode = stateCode ?? string.Empty;

            if (TryGetInt64(orderElement, "currentStatusHistoryId", out long historyId) && historyId > 0)
                order.CurrentStatusHistoryId = historyId;

            if (order.CurrentStatusHistoryId == 0)
                order.CurrentStatusHistoryId = ExtractLatestHistoryId(orderElement);

            if (TryGetPropertyIgnoreCase(orderElement, "hasResult", out JsonElement hasResultProp) &&
                hasResultProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                order.HasResult = hasResultProp.GetBoolean();
            }

            List<ResultFileInfo> files = ExtractResultFiles(orderElement);
            if (files.Count > 0)
            {
                order.ResultFiles = files;
                order.HasResult = true;
            }
        }

        private static long ExtractLatestHistoryId(JsonElement orderElement)
        {
            long latest = 0;
            foreach (string arrayName in new[] { "orderStatusHistories", "statusHistories", "orderStatusHistory" })
            {
                if (!TryGetPropertyIgnoreCase(orderElement, arrayName, out JsonElement arrayProp) ||
                    arrayProp.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement item in arrayProp.EnumerateArray())
                {
                    if (TryGetInt64(item, "id", out long id) ||
                        TryGetInt64(item, "statusHistoryId", out id) ||
                        TryGetInt64(item, "currentStatusHistoryId", out id))
                    {
                        if (id > latest)
                            latest = id;
                    }
                }
            }

            return latest;
        }

        private static List<ResultFileInfo> ExtractResultFiles(JsonElement orderElement)
        {
            foreach (string name in new[] { "orderResponseFiles", "responseFiles", "resultFiles", "files" })
            {
                if (!TryGetPropertyIgnoreCase(orderElement, name, out JsonElement filesProp) ||
                    filesProp.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var parsed = filesProp
                    .EnumerateArray()
                    .Select(ParseResultFile)
                    .Where(file => file is not null)
                    .Cast<ResultFileInfo>()
                    .GroupBy(file => file.Link, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (parsed.Count > 0)
                    return parsed;
            }

            return new List<ResultFileInfo>();
        }

        private static ResultFileInfo? ParseResultFile(JsonElement file)
        {
            string? link = null;
            foreach (string linkName in new[] { "link", "url", "fileLink", "terrabyteLink" })
            {
                if (TryGetString(file, linkName, out string? value) && !string.IsNullOrWhiteSpace(value))
                {
                    link = value;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(link))
                return null;

            string fileName = string.Empty;
            if (TryGetString(file, "fileName", out string? fn) ||
                TryGetString(file, "name", out fn) ||
                TryGetString(file, "mnemonic", out fn))
            {
                fileName = fn ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(fileName))
            {
                try
                {
                    (string mnemonic, _) = ApiClient.ParseAttachmentLink(link);
                    fileName = mnemonic;
                }
                catch
                {
                    fileName = "result";
                }
            }

            return new ResultFileInfo
            {
                FileName = fileName,
                Link = link
            };
        }

        private static bool TryGetString(JsonElement element, string name, out string? value)
        {
            value = null;
            if (!TryGetPropertyIgnoreCase(element, name, out JsonElement prop))
                return false;

            if (prop.ValueKind == JsonValueKind.String)
            {
                value = prop.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }

            if (prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetRawText();
                return true;
            }

            return false;
        }

        private static bool TryGetInt64(JsonElement element, string name, out long value)
        {
            value = 0;
            if (!TryGetPropertyIgnoreCase(element, name, out JsonElement prop))
                return false;

            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
                return true;

            if (prop.ValueKind == JsonValueKind.String &&
                long.TryParse(prop.GetString(), out value))
            {
                return true;
            }

            return false;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}

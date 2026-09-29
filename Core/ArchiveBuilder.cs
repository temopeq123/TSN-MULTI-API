using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TSN_MULTI_API.Core;

public enum SignatureType
{
    Ukep,
    Unep
}

public sealed class ArchiveBuildResult
{
    public required byte[] ArchiveBytes { get; init; }
    public required IReadOnlyDictionary<string, byte[]> Files { get; init; }
}

public class ArchiveBuilder
{
    private static readonly TimeZoneInfo MoscowTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");

    public ArchiveBuildResult BuildArchive(
        IReadOnlyCollection<string> filePaths,
        string snils,
        string description,
        string thumbprint,
        SignatureType signatureType)
    {
        if (filePaths.Count is < 1 or > 20)
            throw new ArgumentException("В пакете должно быть от 1 до 20 документов.", nameof(filePaths));

        string cleanSnils = new string((snils ?? string.Empty).Where(char.IsDigit).ToArray());
        if (cleanSnils.Length != 11)
            throw new ArgumentException("СНИЛС должен содержать ровно 11 цифр.", nameof(snils));

        description = (description ?? string.Empty).Trim();
        if (description.Length == 0)
            throw new ArgumentException("Описание документа не заполнено.", nameof(description));

        // В XSD Госключа Snils задан маской "xxx-xxx-xxx xx". Нельзя передавать
        // только цифры: такая заявка может быть зарегистрирована порталом, но не
        // будет сопоставлена с получателем при обработке Госключом.
        string snilsForRequest = FormatSnils(cleanSnils);
        byte[] xmlBytes = Encoding.UTF8.GetBytes(GenerateReqXml(snilsForRequest, description, signatureType));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["req.xml"] = xmlBytes
        };

        foreach (string filePath in filePaths)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Выбранный файл не найден.", filePath);

            string fileName = Path.GetFileName(filePath);
            if (files.ContainsKey(fileName))
                throw new ArgumentException($"В пакете повторяется имя файла: {fileName}.", nameof(filePaths));

            byte[] fileBytes = File.ReadAllBytes(filePath);
            if (fileBytes.Length == 0)
                throw new InvalidDataException($"Выбранный файл пустой: {fileName}.");
            files[fileName] = fileBytes;
        }

        // Тип УНЭП/УКЭП задаёт вид сведений в req.xml и тип подписи
        // получателя. Все вложения, включая req.xml, по требованиям API
        // должны сопровождаться УКЭП отправителя.
        foreach ((string name, byte[] contents) in files.Where(x => !x.Key.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)).ToList())
            files[$"{name}.sig"] = CryptoSigner.SignDetached(contents, thumbprint);

        using var memoryStream = new MemoryStream();
        using (ZipArchive zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] contents) in files)
                AddFileToZip(zipArchive, name, contents);
        }

        return new ArchiveBuildResult
        {
            ArchiveBytes = memoryStream.ToArray(),
            Files = files
        };
    }

    private static void AddFileToZip(ZipArchive archive, string entryName, byte[] data)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream entryStream = entry.Open();
        entryStream.Write(data, 0, data.Length);
    }

    private static string FormatSnils(string digits) =>
        $"{digits[..3]}-{digits.Substring(3, 3)}-{digits.Substring(6, 3)} {digits[9..]}";

    private static string GenerateReqXml(string snils, string description, SignatureType signatureType)
    {
        string ns = signatureType == SignatureType.Unep
            ? "urn://mpkey.gosuslugi.ru/sign_document/1.0.0"
            : "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0";
        using var stringWriter = new Utf8StringWriter();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using (XmlWriter writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartElement("ns", "SignRequest", ns);
            writer.WriteElementString("ns", "Snils", ns, snils);
            writer.WriteElementString(
                "ns",
                "SignExpiration",
                ns,
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MoscowTimeZone)
                    .AddHours(23)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"));
            writer.WriteElementString("ns", "Description", ns, description);

            WriteAttribute(writer, ns, "orgName", "ТСН САПОЖОК");
            WriteAttribute(writer, ns, "orgINN", "5011013466");
            writer.WriteEndElement();
        }

        return stringWriter.ToString();
    }

    private static void WriteAttribute(XmlWriter writer, string ns, string name, string value)
    {
        writer.WriteStartElement("ns", "Attribute", ns);
        writer.WriteElementString("ns", "AttributeName", ns, name);
        writer.WriteElementString("ns", "AttributeValue", ns, value);
        writer.WriteEndElement();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}

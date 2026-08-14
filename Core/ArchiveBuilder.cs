using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace TSN_MULTI_API.Core;

public class ArchiveBuilder
{
    private static readonly TimeZoneInfo MoscowTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");

    public byte[] BuildArchive(string filePath, string snils, string description, string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Не указан путь к документу.", nameof(filePath));
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Файл не найден: {filePath}", filePath);

        string cleanSnils = new string((snils ?? string.Empty).Where(char.IsDigit).ToArray());
        if (cleanSnils.Length != 11)
            throw new ArgumentException("СНИЛС должен содержать ровно 11 цифр.", nameof(snils));

        description = (description ?? string.Empty).Trim();
        if (description.Length == 0)
            throw new ArgumentException("Описание документа не заполнено.", nameof(description));

        string fileName = Path.GetFileName(filePath);
        byte[] fileBytes = File.ReadAllBytes(filePath);
        if (fileBytes.Length == 0)
            throw new InvalidDataException("Выбранный файл пустой.");

        // В XSD Госключа Snils задан маской "xxx-xxx-xxx xx". Нельзя передавать
        // только цифры: такая заявка может быть зарегистрирована порталом, но не
        // будет сопоставлена с получателем при обработке Госключом.
        string snilsForRequest = FormatSnils(cleanSnils);
        byte[] xmlBytes = Encoding.UTF8.GetBytes(GenerateReqXml(snilsForRequest, description));

        // Создаём обе подписи через один и тот же CryptoPro-код.
        byte[] fileSignature = CryptoSigner.SignDetached(fileBytes, thumbprint);
        byte[] xmlSignature = CryptoSigner.SignDetached(xmlBytes, thumbprint);

        using var memoryStream = new MemoryStream();
        using (ZipArchive zipArchive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddFileToZip(zipArchive, fileName, fileBytes);
            AddFileToZip(zipArchive, $"{fileName}.sig", fileSignature);
            AddFileToZip(zipArchive, "req.xml", xmlBytes);
            AddFileToZip(zipArchive, "req.xml.sig", xmlSignature);
        }

        return memoryStream.ToArray();
    }

    private static void AddFileToZip(ZipArchive archive, string entryName, byte[] data)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using Stream entryStream = entry.Open();
        entryStream.Write(data, 0, data.Length);
    }

    private static string FormatSnils(string digits) =>
        $"{digits[..3]}-{digits.Substring(3, 3)}-{digits.Substring(6, 3)} {digits[9..]}";

    private static string GenerateReqXml(string snils, string description)
    {
        using var stringWriter = new Utf8StringWriter();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };

        using (XmlWriter writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartElement("ns", "SignRequest", "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0");
            writer.WriteElementString("ns", "Snils", "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0", snils);
            writer.WriteElementString(
                "ns",
                "SignExpiration",
                "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0",
                TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, MoscowTimeZone)
                    .AddHours(23)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz"));
            writer.WriteElementString("ns", "Description", "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0", description);

            WriteAttribute(writer, "orgName", "ТСН САПОЖОК");
            WriteAttribute(writer, "orgINN", "5011013466");
            writer.WriteEndElement();
        }

        return stringWriter.ToString();
    }

    private static void WriteAttribute(XmlWriter writer, string name, string value)
    {
        const string ns = "urn://mpkey.gosuslugi.ru/sign_document_ukep/1.0.0";
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

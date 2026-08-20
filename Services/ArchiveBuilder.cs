using System.IO;
using System.IO.Compression;
using System.Text;

namespace TsnSapozhok.Services
{
    // Переименовали класс, чтобы избежать конфликта с ArchiveBuilder из Госключа
    public class FsspZipBuilder
    {
        public byte[] CreateZip(string reqXml, string pievXml)
        {
            using var memoryStream = new MemoryStream();

            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                var reqEntry = archive.CreateEntry("req.xml", CompressionLevel.Optimal);
                using (var reqStream = reqEntry.Open())
                using (var writer = new StreamWriter(reqStream, Encoding.UTF8))
                {
                    writer.Write(reqXml);
                }

                var pievEntry = archive.CreateEntry("piev_epgu.xml", CompressionLevel.Optimal);
                using (var pievStream = pievEntry.Open())
                using (var writer = new StreamWriter(pievStream, Encoding.UTF8))
                {
                    writer.Write(pievXml);
                }
            }

            return memoryStream.ToArray();
        }
    }
}
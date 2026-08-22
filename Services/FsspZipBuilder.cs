using System.IO;
using System.IO.Compression;
using System.Text;

namespace TSN_MULTI_API.Services
{
    public class FsspZipBuilder
    {
        public byte[] CreateZip(string reqXml, string pievXml)
        {
            using var memoryStream = new MemoryStream();

            // Создаем архив в памяти
            using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
            {
                // 1. Добавляем служебный файл req.xml
                var reqEntry = archive.CreateEntry("req.xml", CompressionLevel.Optimal);
                using (var reqStream = reqEntry.Open())
                using (var writer = new StreamWriter(reqStream, Encoding.UTF8))
                {
                    writer.Write(reqXml);
                }

                // 2. Добавляем файл с бизнес-данными piev_epgu.xml
                var pievEntry = archive.CreateEntry("piev_epgu.xml", CompressionLevel.Optimal);
                using (var pievStream = pievEntry.Open())
                using (var writer = new StreamWriter(pievStream, Encoding.UTF8))
                {
                    writer.Write(pievXml);
                }
            }

            // Возвращаем готовый архив в виде массива байтов
            return memoryStream.ToArray();
        }
    }
}
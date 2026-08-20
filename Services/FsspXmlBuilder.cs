using System;
using System.Xml.Linq;

namespace TSN_MULTI_API.Services
{
    public class FsspServiceConfig
    {
        public required string EServiceCode { get; set; }
        public required string ServiceCode { get; set; }
        public required string TargetCode { get; set; }
        public required string ReceiverId { get; set; }
    }

    public static class FsspConfigProvider
    {
        public static FsspServiceConfig GetConfig(string eServiceCode, bool isLegalEntity)
        {
            return eServiceCode switch
            {
                "60010153" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "10001449665", TargetCode = "10001505301", ReceiverId = "FSSP10" },
                "10000000352" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "10001449665", TargetCode = "10003818851", ReceiverId = isLegalEntity ? "FSSP08" : "FSSP07" },
                "10000000367" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "-10002600000", TargetCode = "-10002600000", ReceiverId = "FSSP09" },
                _ => throw new ArgumentException("Неизвестный код услуги ФССП")
            };
        }
    }

    public class FsspXmlBuilder
    {
        public string BuildReqXml(string orderId, FsspServiceConfig config, string departmentCode = "00000")
        {
            XNamespace fssp = "urn://x-artifacts-fssp-ru/mvv/smev3/epgu/1.0.1";
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(fssp + "EPGURequest",
                    new XAttribute(XNamespace.Xmlns + "fssp", fssp.NamespaceName),
                    new XAttribute("Env", "SVCDEV"),
                    new XElement(fssp + "DataRequest",
                        new XElement(fssp + "OrderId", orderId),
                        new XElement(fssp + "Date", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz")),
                        new XElement(fssp + "Department", "ФССП"),
                        new XElement(fssp + "DepartmentCode", departmentCode),
                        new XElement(fssp + "ReceiverID", config.ReceiverId),
                        new XElement(fssp + "ServiceCode", config.ServiceCode),
                        new XElement(fssp + "TargetCode", config.TargetCode),
                        new XElement(fssp + "StatementDate", DateTime.Now.ToString("yyyy-MM-dd"))
                    )
                )
            );
            return doc.ToString();
        }

        public string BuildPievEpguExistInteractive(string externalKey, string authorName, string authorSnils, string orgName, string orgInn, string orgOgrn, string orgAddress, string authorBorn)
        {
            XNamespace fssp = "http://www.fssprus.ru/namespace/incoming/2019/1";
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(fssp + "IRequest",
                    new XAttribute(XNamespace.Xmlns + "fssp", fssp.NamespaceName),
                    new XElement(fssp + "ExternalKey", externalKey),
                    new XElement(fssp + "DocType", "I_IP_EXIST_INTERACTIVE"),
                    new XElement(fssp + "DocName", "Заявление о предоставлении информации о наличии исполнительного производства из банка данных"),
                    new XElement(fssp + "DocDate", DateTime.Now.ToString("yyyy-MM-dd")),
                    new XElement(fssp + "IncludeAll", "true"),
                    new XElement(fssp + "ComplainerType", "2"),
                    new XElement(fssp + "AuthorName", authorName),
                    new XElement(fssp + "ComplainerGender", "1"),
                    new XElement(fssp + "AuthorBorn", authorBorn),
                    new XElement(fssp + "AuthorSnils", authorSnils),
                    new XElement(fssp + "AuthorBackAddrType", "ЕПГУ"),
                    new XElement(fssp + "AuthorBackAddr", authorSnils),
                    new XElement(fssp + "TrusteeDoctype", "07"),
                    new XElement(fssp + "TrusteeDivision", orgName),
                    new XElement(fssp + "TrusteeDocnumber", "-"),
                    new XElement(fssp + "TrusteeDocdate", DateTime.Now.ToString("yyyy-MM-dd")),
                    new XElement(fssp + "TrusteeName", orgName),
                    new XElement(fssp + "TrusteeAddress", orgAddress),
                    new XElement(fssp + "TrusteeInn", orgInn),
                    new XElement(fssp + "TrusteeOGRN", orgOgrn),
                    new XElement(fssp + "SimpleDigSignature", authorSnils),
                    new XElement(fssp + "Sendlist",
                        new XElement(fssp + "Receiver", "ФССП"),
                        new XElement(fssp + "ReceiverDivisionCode", "00000"),
                        new XElement(fssp + "ReceiverAddrType", "ВЕБ-СЕРВИС"),
                        new XElement(fssp + "ReceiverAddr", "00000")
                    )
                )
            );
            return doc.ToString();
        }

        public string BuildPievEpguCourseIp(string externalKey, string deloNum, string authorName, string authorSnils, string orgName, string orgInn, string orgOgrn, string orgAddress, string authorBorn)
        {
            XNamespace fssp = "http://www.fssprus.ru/namespace/incoming/2019/1";
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(fssp + "IRequest",
                    new XAttribute(XNamespace.Xmlns + "fssp", fssp.NamespaceName),
                    new XElement(fssp + "ExternalKey", externalKey),
                    new XElement(fssp + "DocType", "I_IPSIDE_FSSP_INTERACTIVE"),
                    new XElement(fssp + "DocName", "Заявление о предоставлении информации о ходе исполнительного производства из банка данных"),
                    new XElement(fssp + "DocDate", DateTime.Now.ToString("yyyy-MM-dd")),
                    new XElement(fssp + "DeloNum", deloNum),
                    new XElement(fssp + "ComplainerType", "2"),
                    new XElement(fssp + "AuthorName", authorName),
                    new XElement(fssp + "ComplainerGender", "1"),
                    new XElement(fssp + "AuthorBorn", authorBorn),
                    new XElement(fssp + "AuthorSnils", authorSnils),
                    new XElement(fssp + "AuthorBackAddrType", "ЕПГУ"),
                    new XElement(fssp + "AuthorBackAddr", authorSnils),
                    new XElement(fssp + "TrusteeDoctype", "07"),
                    new XElement(fssp + "TrusteeDivision", orgName),
                    new XElement(fssp + "TrusteeDocnumber", "-"),
                    new XElement(fssp + "TrusteeDocdate", DateTime.Now.ToString("yyyy-MM-dd")),
                    new XElement(fssp + "TrusteeName", orgName),
                    new XElement(fssp + "TrusteeAddress", orgAddress),
                    new XElement(fssp + "TrusteeInn", orgInn),
                    new XElement(fssp + "TrusteeOGRN", orgOgrn),
                    new XElement(fssp + "SimpleDigSignature", authorSnils),
                    new XElement(fssp + "Sendlist",
                        new XElement(fssp + "Receiver", "ФССП"),
                        new XElement(fssp + "ReceiverDivisionCode", "00000"),
                        new XElement(fssp + "ReceiverAddrType", "ВЕБ-СЕРВИС"),
                        new XElement(fssp + "ReceiverAddr", "00000")
                    )
                )
            );
            return doc.ToString();
        }

        public string BuildPievEpguPetition(string externalKey, string docType, string docName, string text, string ipNum, string authorName, string authorSnils, string authorBorn, string authorPhone, string authorAddress)
        {
            XNamespace fssp = "http://www.fssp.gov.ru/namespace/Petition/2022/1";
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(fssp + "Petition",
                    new XAttribute(XNamespace.Xmlns + "fssp", fssp.NamespaceName),
                    new XElement(fssp + "ExternalKey", externalKey),
                    new XElement(fssp + "DocType", docType),
                    new XElement(fssp + "DocName", docName),
                    new XElement(fssp + "DocDate", DateTime.Now.ToString("yyyy-MM-dd")),
                    new XElement(fssp + "Text", text),
                    new XElement(fssp + "IpNum", ipNum),
                    new XElement(fssp + "ComplainerType", "2"),
                    new XElement(fssp + "AuthorName", authorName),
                    new XElement(fssp + "ComplainerGender", "1"),
                    new XElement(fssp + "AuthorAddress", authorAddress),
                    new XElement(fssp + "AuthorBorn", authorBorn),
                    new XElement(fssp + "AuthorSnils", authorSnils),
                    new XElement(fssp + "AuthorPhone", authorPhone),
                    new XElement(fssp + "AuthorBackAddrType", "ЕПГУ"),
                    new XElement(fssp + "AuthorBackAddr", authorSnils),
                    new XElement(fssp + "SimpleDigSignature", authorSnils),
                    new XElement(fssp + "SendList",
                        new XElement(fssp + "Receiver", "ФССП"),
                        new XElement(fssp + "ReceiverAddrType", "ВЕБ-СЕРВИС"),
                        new XElement(fssp + "ReceiverAddr", "00000")
                    )
                )
            );
            return doc.ToString();
        }
    }
}
using System;

namespace TsnSapozhok.Services
{
    public class FsspServiceConfig
    {
        public string EServiceCode { get; set; }
        public string ServiceCode { get; set; }
        public string TargetCode { get; set; }
        public string ReceiverId { get; set; }
    }

    public static class FsspConfigProvider
    {
        public static FsspServiceConfig GetConfig(string eServiceCode, bool isLegalEntity)
        {
            return eServiceCode switch
            {
                // Наличие ИП (60010153)
                "60010153" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "10001449665", TargetCode = "10001505301", ReceiverId = "FSSP10" },

                // Ход ИП (10000000352)
                "10000000352" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "10001449665", TargetCode = "10003818851", ReceiverId = isLegalEntity ? "FSSP08" : "FSSP07" },

                // Подача заявлений и ходатайств (10000000367)
                "10000000367" => new FsspServiceConfig { EServiceCode = eServiceCode, ServiceCode = "-10002600000", TargetCode = "-10002600000", ReceiverId = "FSSP09" },

                _ => throw new ArgumentException("Неизвестный код услуги ФССП")
            };
        }
    }
}
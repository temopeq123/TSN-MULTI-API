using CryptoPro.Security.Cryptography.Pkcs;
using CryptoPro.Security.Cryptography.X509Certificates;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TSN_MULTI_API.Core;

/// <summary>
/// Единая точка работы с сертификатом и формирования detached CMS/PKCS#7.
/// Для подписи используется именно хранилище CryptoPro, а не только Windows X509Store.
/// </summary>
public static class CryptoSigner
{
    private const string Gost256PublicKeyOid = "1.2.643.7.1.1.1.1";
    private const string Gost512PublicKeyOid = "1.2.643.7.1.1.1.2";
    private const string Gost256DigestOid = "1.2.643.7.1.1.2.2";
    private const string Gost512DigestOid = "1.2.643.7.1.1.2.3";

    public static SignatureVerificationResult VerifyDetachedSignature(byte[] content, byte[] signature)
    {
        try
        {
            // 1. Готовим чистый Base64
            string sigBase64 = Convert.ToBase64String(signature);
            string sigText = System.Text.Encoding.UTF8.GetString(signature).Trim();
            if (sigText.StartsWith("\uFEFF")) sigText = sigText.Substring(1);

            if (sigText.StartsWith("-----BEGIN") ||
               (sigText.Length > 0 && sigText.All(c => char.IsLetterOrDigit(c) || c == '+' || c == '/' || c == '=' || char.IsWhiteSpace(c))))
            {
                var lines = sigText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                   .Where(l => !l.StartsWith("-----"));
                sigBase64 = string.Join("", lines);
            }

            string contentBase64 = Convert.ToBase64String(content);

            // 2. Обращаемся к CAdESCOM
            Type typeCades = Type.GetTypeFromProgID("CAdESCOM.CadesSignedData");
            if (typeCades == null)
            {
                return new SignatureVerificationResult(false,
                    "Для нативной проверки установите 'КриптоПро ЭЦП Browser plug-in' (CAdESCOM).");
            }

            dynamic cadesSignedData = Activator.CreateInstance(typeCades);
            cadesSignedData.ContentEncoding = 1; // CADESCOM_BASE64_TO_BINARY
            cadesSignedData.Content = contentBase64;

            // 3. Проверяем подпись, отдавая приоритет штампу времени
            bool isCadesT = false;
            try
            {
                // Пробуем проверить полноценную подпись со штампом времени (CAdES-T = 5)
                cadesSignedData.VerifyCades(sigBase64, 5, true);
                isCadesT = true;
            }
            catch
            {
                // Умный фолбэк: если нет доверия к серверу времени Госключа, проверяем хотя бы базовую математику (CAdES-BES = 1)
                cadesSignedData.VerifyCades(sigBase64, 1, true);
            }

            // 4. Извлекаем данные о подписантах и точное время
            var signersInfo = new List<string>();
            foreach (dynamic signer in cadesSignedData.Signers)
            {
                // Парсим имя
                string subject = signer.Certificate.SubjectName;
                var match = System.Text.RegularExpressions.Regex.Match(subject, @"CN=([^,]+)");
                string cleanName = match.Success ? match.Groups[1].Value.Trim() : subject;
                string validTo = signer.Certificate.ValidToDate.ToString("dd.MM.yyyy");

                // Извлекаем время подписания с красивым форматированием
                string timeStr = "";
                try
                {
                    DateTime tsaTime = signer.SignatureTimeStampTime;
                    timeStr = $"\n      ⏱ Штамп времени: {tsaTime:dd.MM.yyyy HH:mm:ss} (TSA)";
                }
                catch
                {
                    try
                    {
                        DateTime localTime = signer.SigningTime;
                        timeStr = $"\n      ⏳ Время ПК: {localTime:dd.MM.yyyy HH:mm:ss} (не подтверждено)";
                    }
                    catch { }
                }

                signersInfo.Add($"{cleanName} (до {validTo}){timeStr}");
            }

            string formatName = isCadesT ? "CAdES-T" : "CAdES-BES";
            return new SignatureVerificationResult(true, $"Формат: {formatName}\n   👤 Подписант: {string.Join("\n                 ", signersInfo)}");
        }
        catch (Exception ex)
        {
            return new SignatureVerificationResult(false, $"Не прошла нативная проверка: {ex.Message}");
        }
    }

    public static string NormalizeThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new ArgumentException("Не указан отпечаток сертификата.", nameof(thumbprint));

        return thumbprint
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("\u200e", string.Empty, StringComparison.Ordinal)
            .Replace("\u200f", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();
    }

    public static CpX509Certificate2 FindCertificate(string thumbprint)
    {
        string clean = NormalizeThumbprint(thumbprint);
        Exception? lastError = null;

        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new CpX509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);

                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, clean, false);
                if (matches.Count == 0)
                    continue;

                CpX509Certificate2 cert = matches[0];
                if (!cert.HasPrivateKey)
                {
                    cert.Dispose();
                    throw new CryptographicException(
                        $"Сертификат {clean} найден в {location}, но CryptoPro не видит у него закрытый ключ. " +
                        "Проверьте привязку сертификата к контейнеру закрытого ключа в КриптоПро CSP.");
                }

                if (cert.NotBefore > DateTime.Now || cert.NotAfter < DateTime.Now)
                {
                    DateTime from = cert.NotBefore;
                    DateTime to = cert.NotAfter;
                    cert.Dispose();
                    throw new CryptographicException(
                        $"Сертификат {clean} недействителен. Срок: {from:dd.MM.yyyy HH:mm:ss} — {to:dd.MM.yyyy HH:mm:ss}.");
                }

                return cert;
            }
            catch (Exception ex) when (ex is CryptographicException || ex is InvalidOperationException)
            {
                lastError = ex;
            }
        }

        // Диагностический fallback
        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, clean, false);

                if (matches.Count > 0)
                {
                    using X509Certificate2 cert = matches[0];
                    string keyState = cert.HasPrivateKey ? "закрытый ключ виден Windows" : "закрытого ключа нет";
                    throw new CryptographicException(
                        $"Сертификат {clean} найден в Windows-хранилище {location}, но CryptoPro не смог его использовать ({keyState}). " +
                        "Установите/проверьте сертификат и контейнер закрытого ключа в КриптоПро CSP.");
                }
            }
            catch (CryptographicException ex)
            {
                lastError = ex;
            }
        }

        throw new CryptographicException(
            $"Сертификат с отпечатком {clean} не найден в MY хранилищах текущего пользователя и локального компьютера.",
            lastError);
    }

    public static byte[] SignDetached(byte[] data, string thumbprint)
    {
        if (data is null || data.Length == 0)
            throw new ArgumentException("Нельзя подписать пустые данные.", nameof(data));

        string cleanThumbprint = NormalizeThumbprint(thumbprint);

        foreach (StoreLocation location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new CpX509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Find(
                    X509FindType.FindByThumbprint, cleanThumbprint, false);

                if (certs.Count == 0)
                    continue;

                using CpX509Certificate2 cert = certs[0];
                if (!cert.HasPrivateKey)
                    throw new CryptographicException(
                        $"Сертификат {cleanThumbprint} найден в {location}, но закрытый ключ недоступен.");

                string publicKeyOid = cert.PublicKey?.Oid?.Value ?? string.Empty;
                if (publicKeyOid != Gost256PublicKeyOid && publicKeyOid != Gost512PublicKeyOid)
                    throw new CryptographicException(
                        $"Сертификат {cleanThumbprint} имеет неподдерживаемый алгоритм открытого ключа OID={publicKeyOid}.");

                return SignWithCryptoProCms(data, cert, cleanThumbprint, location);
            }
            catch (CryptographicException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CryptographicException(
                    $"Ошибка доступа к сертификату {cleanThumbprint} в {location}: {ex.Message}", ex);
            }
        }

        throw new CryptographicException(
            $"Сертификат с отпечатком {cleanThumbprint} не найден в MY хранилищах.");
    }

    private static byte[] SignWithCryptoProCms(
        byte[] data,
        CpX509Certificate2 cert,
        string thumbprint,
        StoreLocation location)
    {
        try
        {
            var contentInfo = new ContentInfo(data);
            var signedCms = new CpSignedCms(contentInfo, detached: true);
            var cmsSigner = new CpCmsSigner(cert)
            {
                IncludeOption = X509IncludeOption.EndCertOnly
            };

            cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

            string publicKeyOid = cert.PublicKey?.Oid?.Value ?? string.Empty;
            cmsSigner.DigestAlgorithm = new Oid(
                publicKeyOid == Gost512PublicKeyOid ? Gost512DigestOid : Gost256DigestOid);

            signedCms.ComputeSignature(cmsSigner);
            byte[] encoded = signedCms.Encode();

            if (encoded.Length == 0)
                throw new CryptographicException("CpSignedCms вернул пустую подпись.");

            return encoded;
        }
        catch (Exception ex) when (ex is CryptographicException || ex is InvalidOperationException)
        {
            throw new CryptographicException(
                $"Не удалось создать CMS/PKCS#7 detached для сертификата {thumbprint} " +
                $"(хранилище {location}). Причина: {ex.Message}", ex);
        }
    }

    public static string ToEsiaSignatureQueryValue(byte[] signature)
    {
        if (signature is null || signature.Length == 0)
            throw new ArgumentException("Подпись пуста.", nameof(signature));

        string base64 = Convert.ToBase64String(signature).TrimEnd('=');
        return base64
            .Replace("+", "%2b", StringComparison.Ordinal)
            .Replace("/", "%2f", StringComparison.Ordinal);
    }
}

public sealed record SignatureVerificationResult(bool IsValid, string Details);
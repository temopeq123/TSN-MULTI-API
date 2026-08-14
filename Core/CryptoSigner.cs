using System;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using CryptoPro.Security.Cryptography.Pkcs;
using CryptoPro.Security.Cryptography.X509Certificates;

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

        // Диагностический fallback: проверяем Windows-хранилища и состояние HasPrivateKey.
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
                        "Установите/проверьте сертификат и контейнер закрытого ключа в КриптоПро CSP." );
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

        // Для CMS с ГОСТ-ключом используем классы CryptoPro. Обычный SignedCms
        // может создать локально проверяемую подпись, которую ЕСИА не примет.
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
            // detached=true — содержимое data не вкладывается в CMS.
            var contentInfo = new ContentInfo(data);
            var signedCms = new CpSignedCms(contentInfo, detached: true);
            var cmsSigner = new CpCmsSigner(cert)
            {
                IncludeOption = X509IncludeOption.EndCertOnly
            };

            // ЕСИА принимает только свежую подпись: атрибут signingTime обязателен
            // для проверки ограничения «не старше 24 часов».
            cmsSigner.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

            // Явно задаём соответствующий сертификату ГОСТ digest.
            string publicKeyOid = cert.PublicKey?.Oid?.Value ?? string.Empty;
            cmsSigner.DigestAlgorithm = new Oid(
                publicKeyOid == Gost512PublicKeyOid ? Gost512DigestOid : Gost256DigestOid);

            signedCms.ComputeSignature(cmsSigner);
            byte[] encoded = signedCms.Encode();

            if (encoded.Length == 0)
                throw new CryptographicException("CpSignedCms вернул пустую подпись.");

            // Проверяем именно ту detached CMS, которую собираемся отправить.
            var verifier = new CpSignedCms(new ContentInfo(data), detached: true);
            verifier.Decode(encoded);
            verifier.CheckSignature(verifySignatureOnly: true);

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

        // Для ЕСИА нужен urlSafeBase64: padding '=' удаляется,
        // а символы + и / передаются в percent-encoded виде.
        string base64 = Convert.ToBase64String(signature).TrimEnd('=');
        return base64
            .Replace("+", "%2b", StringComparison.Ordinal)
            .Replace("/", "%2f", StringComparison.Ordinal);
    }
}

package dev.midfielder.core.crypto

import org.bouncycastle.asn1.x500.X500Name
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Date

/**
 * This device's long-term identity: an EC P-256 key and a self-signed certificate.
 * Peers pin the certificate's [fingerprint]; there is no CA.
 */
public class DeviceIdentity(
    public val privateKey: PrivateKey,
    public val certificate: X509Certificate,
) {
    public val fingerprint: Fingerprint = Fingerprint.of(certificate)
    public val deviceId: String get() = fingerprint.deviceId

    /** Serializes to PKCS#12. Platforms protect [password] (Android Keystore / Windows DPAPI). */
    public fun toPkcs12(password: CharArray): ByteArray {
        val ks = KeyStore.getInstance("PKCS12")
        ks.load(null, null)
        ks.setKeyEntry(ALIAS, privateKey, password, arrayOf(certificate))
        return ByteArrayOutputStream().also { ks.store(it, password) }.toByteArray()
    }

    public companion object {
        public const val ALIAS: String = "midfielder"
        private const val VALIDITY_MS = 20L * 365 * 24 * 60 * 60 * 1000

        public fun generate(random: SecureRandom = SecureRandom()): DeviceIdentity {
            val kpg = KeyPairGenerator.getInstance("EC")
            kpg.initialize(ECGenParameterSpec("secp256r1"), random)
            val keyPair = kpg.generateKeyPair()
            val name = X500Name("CN=midfielder")
            val now = System.currentTimeMillis()
            val serial = BigInteger(63, random).add(BigInteger.ONE)
            val builder = JcaX509v3CertificateBuilder(
                name, serial, Date(now - 24L * 60 * 60 * 1000), Date(now + VALIDITY_MS), name, keyPair.public,
            )
            val signer = JcaContentSignerBuilder("SHA256withECDSA").build(keyPair.private)
            val cert = JcaX509CertificateConverter().getCertificate(builder.build(signer))
            return DeviceIdentity(keyPair.private, cert)
        }

        public fun fromPkcs12(bytes: ByteArray, password: CharArray): DeviceIdentity {
            val ks = KeyStore.getInstance("PKCS12")
            ks.load(ByteArrayInputStream(bytes), password)
            val key = ks.getKey(ALIAS, password) as? PrivateKey ?: error("no private key in identity store")
            val cert = ks.getCertificate(ALIAS) as? X509Certificate ?: error("no certificate in identity store")
            return DeviceIdentity(key, cert)
        }
    }
}

package dev.midfielder.core.net

import dev.midfielder.core.crypto.DeviceIdentity
import dev.midfielder.core.crypto.Fingerprint
import java.net.Socket
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509ExtendedTrustManager

/**
 * Trust is decided only by certificate fingerprint pinning; there are no CAs and no hostnames.
 * [accept] is consulted on every handshake, so trust changes (pair/unpair) apply to new connections at once.
 */
internal class PinnedTrustManager(private val accept: (Fingerprint) -> Boolean) : X509ExtendedTrustManager() {
    private fun check(chain: Array<out X509Certificate>?) {
        if (chain.isNullOrEmpty()) throw CertificateException("peer sent no certificate")
        val fp = Fingerprint.of(chain[0])
        if (!accept(fp)) throw CertificateException("untrusted peer ${fp.display()}")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = check(chain)
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
        check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) =
        check(chain)
    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
        check(chain)
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) =
        check(chain)
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

/** Always presents this device's identity, whatever the peer asks for. */
internal class IdentityKeyManager(private val identity: DeviceIdentity) : X509ExtendedKeyManager() {
    private val alias = DeviceIdentity.ALIAS
    override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(alias)
    override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) =
        alias
    override fun chooseEngineClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, e: SSLEngine?) =
        alias
    override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(alias)
    override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) = alias
    override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, e: SSLEngine?) = alias
    override fun getCertificateChain(alias: String?): Array<X509Certificate> = arrayOf(identity.certificate)
    override fun getPrivateKey(alias: String?): PrivateKey = identity.privateKey
}

internal object Tls {
    private val PROTOCOLS = arrayOf("TLSv1.3")

    fun context(identity: DeviceIdentity, accept: (Fingerprint) -> Boolean): SSLContext =
        SSLContext.getInstance("TLSv1.3").apply {
            init(arrayOf(IdentityKeyManager(identity)), arrayOf(PinnedTrustManager(accept)), SecureRandom())
        }

    fun configure(socket: SSLSocket) {
        socket.enabledProtocols = PROTOCOLS
    }

    fun peerFingerprint(socket: SSLSocket): Fingerprint {
        val certs = socket.session.peerCertificates
        if (certs.isEmpty()) throw CertificateException("no peer certificate")
        return Fingerprint.of(certs[0])
    }
}

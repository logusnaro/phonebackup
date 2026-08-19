package com.company.phonebackup

import android.content.Context
import android.os.Build
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.util.UUID
import android.util.Base64
import java.util.concurrent.TimeUnit
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.SSLContext
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager
import java.security.cert.X509Certificate
import java.security.cert.CertificateException

@Serializable data class PairRequest(val ticketId: String, val deviceName: String, val model: String, val androidVersion: String, val devicePublicKey: String = "")
@Serializable data class PairResponse(val deviceId: String, val memberId: String, val deviceToken: String, val serverUrl: String, val certificateSha256: String, val apiVersion: String)
@Serializable data class StartResponse(val syncRunId: String)
@Serializable data class FinishPayload(val syncRunId: String, val status: String, val filesSeen: Int, val filesStored: Int, val error: String?)
@Serializable data class ProgressPayload(val syncRunId: String, val filesProcessed: Int, val filesTotal: Int, val currentFile: String?, val stage: String)

class NetworkClient(private val context: Context, private val store: PairingStore) {
    private val json = Json { ignoreUnknownKeys = true }
    private fun client(config: PairingConfig): OkHttpClient {
        val expectedFingerprint = config.certificateSha256.uppercase()
        val trustManager = object : X509TrustManager {
            override fun getAcceptedIssuers() = arrayOf<X509Certificate>()
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = Unit
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                if (chain.isEmpty()) throw CertificateException("PC 인증서가 없습니다.")
                val actual = MessageDigest.getInstance("SHA-256").digest(chain[0].encoded)
                    .joinToString("") { "%02X".format(it) }
                if (actual != expectedFingerprint) throw CertificateException("PC 인증서 지문이 일치하지 않습니다.")
            }
        }
        val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf<TrustManager>(trustManager), null) }
        return OkHttpClient.Builder().connectTimeout(10, TimeUnit.SECONDS).readTimeout(0, TimeUnit.MILLISECONDS).sslSocketFactory(ssl.socketFactory, trustManager).hostnameVerifier(HostnameVerifier { _, _ -> true }).build()
    }
    fun pair(ticket: PairingTicketInput): PairResponse {
        val payload = PairRequest(ticket.ticketId, Build.MODEL, Build.MODEL, "${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})")
        val config = PairingConfig("pairing", "", ticket.serverUrl, ticket.certificateSha256)
        val request = Request.Builder().url("${ticket.serverUrl}/api/v1/pair/claim").post(json.encodeToString(PairRequest.serializer(), payload).toRequestBody("application/json".toMediaType())).build()
        client(config).newCall(request).execute().use { response -> if (!response.isSuccessful) error("연결 실패: ${response.code}"); return json.decodeFromString(PairResponse.serializer(), response.body!!.string()) }
    }
    fun discoverAndPair(code: String): PairResponse = pair(discover(code))

    private fun discover(code: String): PairingTicketInput {
        val normalized = code.filter { it.isLetterOrDigit() }.uppercase()
        require(normalized.length == 6) { "연결 코드는 영문·숫자 6자리입니다." }
        val nonce = UUID.randomUUID().toString().replace("-", "")
        val request = "PHONEBACKUP_PAIR_V1|$normalized|$nonce".toByteArray(Charsets.UTF_8)
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 1800
            socket.send(DatagramPacket(request, request.size, InetAddress.getByName("255.255.255.255"), 42818))
            val deadline = System.currentTimeMillis() + 2200
            while (System.currentTimeMillis() < deadline) {
                try {
                    val buffer = ByteArray(4096)
                    val packet = DatagramPacket(buffer, buffer.size)
                    socket.receive(packet)
                    val response = json.decodeFromString(DiscoveryResponse.serializer(), String(packet.data, 0, packet.length, Charsets.UTF_8))
                    if (response.nonce == nonce) return PairingTicketInput(response.ticketId, response.serverUrl, response.certificateSha256)
                } catch (_: SocketTimeoutException) { break }
            }
        }
        error("PC를 찾지 못했습니다. PC에서 ‘기기 연결’을 누르고 같은 Wi‑Fi인지 확인하세요.")
    }
    fun start(config: PairingConfig): String {
        val response = authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/sync/start").post(ByteArray(0).toRequestBody()).headers(headers(config)).build()).execute()
        response.use { if (!it.isSuccessful) error("백업 시작 실패: ${it.code}"); return json.decodeFromString(StartResponse.serializer(), it.body!!.string()).syncRunId }
    }
    fun ping(config: PairingConfig) {
        authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/device/ping").headers(headers(config)).get().build()).execute().use {
            if (!it.isSuccessful) error("PC 응답 오류: ${it.code}")
        }
    }
    fun progress(config: PairingConfig, runId: String, processed: Int, total: Int, currentFile: String?, stage: String) {
        val payload = ProgressPayload(runId, processed, total, currentFile, stage)
        val body = json.encodeToString(ProgressPayload.serializer(), payload).toRequestBody("application/json".toMediaType())
        authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/sync/progress").headers(headers(config)).post(body).build()).execute().use {
            if (!it.isSuccessful) error("진행상황 전송 실패: ${it.code}")
        }
    }
    fun upload(config: PairingConfig, runId: String, file: File, relativePath: String, category: String) {
        val relativeB64 = Base64.encodeToString(relativePath.toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
        val nameB64 = Base64.encodeToString(relativePath.substringAfterLast('/').toByteArray(Charsets.UTF_8), Base64.NO_WRAP)
        val requestHeaders = headers(config).newBuilder().add("X-Relative-Path-B64", relativeB64).add("X-SHA256", sha256(file)).add("X-Original-File-Name-B64", nameB64).add("X-Category", category).build()
        val request = Request.Builder().url("${config.serverUrl}/api/v1/sync/file?syncRunId=$runId").headers(requestHeaders).put(file.asRequestBody("application/octet-stream".toMediaType())).build()
        authenticated(config).newCall(request).execute().use { if (!it.isSuccessful) error("파일 업로드 실패 $relativePath: HTTP ${it.code}") }
    }
    fun proposeContacts(config: PairingConfig, contacts: List<ContactDto>) {
        @Serializable data class Snapshot(val deviceId: String, val contacts: List<ContactDto>)
        val body = json.encodeToString(Snapshot.serializer(), Snapshot(config.deviceId, contacts)).toRequestBody("application/json".toMediaType())
        authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/contacts/proposals").headers(headers(config)).post(body).build()).execute().use { if (!it.isSuccessful) error("주소록 업로드 실패: ${it.code}") }
    }
    fun downloadContacts(config: PairingConfig): List<ContactDto> {
        val response = authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/contacts").headers(headers(config)).get().build()).execute()
        response.use { if (!it.isSuccessful) error("주소록 다운로드 실패: ${it.code}"); return json.decodeFromString(it.body!!.string()) }
    }
    fun finish(config: PairingConfig, runId: String, seen: Int, stored: Int, error: String? = null) {
        val body = json.encodeToString(FinishPayload.serializer(), FinishPayload(runId, if (error == null) "Completed" else "Failed", seen, stored, error)).toRequestBody("application/json".toMediaType())
        authenticated(config).newCall(Request.Builder().url("${config.serverUrl}/api/v1/sync/finish").headers(headers(config)).post(body).build()).execute().close()
    }
    private fun authenticated(config: PairingConfig) = client(config)
    private fun headers(config: PairingConfig) = Headers.headersOf("X-Device-Id", config.deviceId, "X-Device-Token", config.token)
    private fun sha256(file: File): String { val digest = MessageDigest.getInstance("SHA-256"); file.inputStream().use { input -> val buf = ByteArray(1024 * 1024); var n: Int; while (input.read(buf).also { n = it } > 0) digest.update(buf, 0, n) }; return digest.digest().joinToString("") { "%02X".format(it) } }
}

@Serializable data class PairingTicketInput(val ticketId: String, val serverUrl: String, val certificateSha256: String)
@Serializable data class DiscoveryResponse(val nonce: String, val ticketId: String, val serverUrl: String, val certificateSha256: String)

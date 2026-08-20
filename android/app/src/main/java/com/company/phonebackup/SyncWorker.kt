package com.company.phonebackup

import android.content.Context
import android.net.Uri
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import androidx.work.workDataOf
import java.io.IOException
import java.net.SocketException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    companion object {
        private const val MAX_FILES_PER_BATCH = 50
        private const val MAX_BYTES_PER_BATCH = 256L * 1024L * 1024L
        private const val STATE_PREFS = "chunked_sync"
    }
    private fun categoryFor(path: String): String {
        val lower = path.lowercase()
        val ext = lower.substringAfterLast('.', "")
        if (lower.contains("tphonecallrecords") || lower.contains("recording") || lower.contains("/call/") || lower.startsWith("call/")) return "recording"
        if (ext in setOf("jpg", "jpeg", "png", "heic", "gif", "webp", "bmp")) return "image"
        if (ext in setOf("mp4", "mov", "avi", "mkv", "wmv")) return "video"
        if (ext in setOf("pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "hwp", "hwpx", "cell", "show", "txt", "csv", "rtf")) return "document"
        if (ext in setOf("m4a", "amr", "3gp", "wav", "mp3", "aac", "ogg", "flac")) return "audio"
        return "other"
    }
    override suspend fun doWork(): Result {
        val store = PairingStore(applicationContext); val config = store.load() ?: return Result.failure(workDataOf("error" to "PC에 먼저 연결해 주세요."))
        val tree = applicationContext.getSharedPreferences("sync", Context.MODE_PRIVATE).getString("treeUri", null)
        val client = NetworkClient(applicationContext, store); val repository = FileRepository(applicationContext)
        val state = applicationContext.getSharedPreferences(STATE_PREFS, Context.MODE_PRIVATE)
        val runKey = "run_${config.deviceId}"; val cursorKey = "cursor_${config.deviceId}"; val processedKey = "processed_${config.deviceId}"; val storedKey = "stored_${config.deviceId}"
        var runId = state.getString(runKey, null) ?: ""
        var processed = state.getInt(processedKey, 0); var stored = state.getInt(storedKey, 0)
        return try {
            if (runId.isBlank()) {
                setProgress(workDataOf("stage" to "PC 연결 중", "processed" to 0, "total" to 0))
                runId = client.start(config)
                state.edit().putString(runKey, runId).putInt(processedKey, 0).putInt(storedKey, 0).remove(cursorKey).apply()
                processed = 0; stored = 0
                if (androidx.core.content.ContextCompat.checkSelfPermission(applicationContext, android.Manifest.permission.READ_CONTACTS) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
                    setProgress(workDataOf("stage" to "주소록 백업 중", "processed" to 0, "total" to 0))
                    runCatching { client.proposeContacts(config, ContactsRepository(applicationContext).readAll()) }
                }
            }
            setProgress(workDataOf("stage" to "파일 목록 확인 중", "processed" to processed, "total" to 0))
            val files = (if (tree.isNullOrBlank()) repository.listDefaultFiles(config.deviceId) else repository.listFiles(Uri.parse(tree))).sortedBy { it.second }
            client.progress(config, runId, processed, files.size, null, "분할 백업 준비")
            val cursor = state.getString(cursorKey, null).orEmpty()
            val firstIndex = if (cursor.isBlank()) 0 else files.indexOfFirst { it.second > cursor }.let { if (it < 0) files.size else it }
            var selected = 0; var selectedBytes = 0L; var lastProcessedPath = cursor
            for (index in firstIndex until files.size) {
                if (selected >= MAX_FILES_PER_BATCH) break
                val (uri, relative) = files[index]
                val estimated = repository.sizeOf(uri)
                if (selected > 0 && estimated > 0 && selectedBytes + estimated > MAX_BYTES_PER_BATCH) break
                setProgress(workDataOf("stage" to "업로드 중", "processed" to processed, "total" to files.size, "currentFile" to relative))
                val temp = repository.copyToTemp(uri)
                try {
                    if (selected > 0 && selectedBytes + temp.length() > MAX_BYTES_PER_BATCH) break
                    selectedBytes += temp.length()
                    val sha = client.sha256ForUpload(temp)
                    if (!client.isKnown(config, relative, sha)) {
                        client.upload(config, runId, temp, relative, categoryFor(relative), sha)
                        stored++
                    }
                    processed++; selected++; lastProcessedPath = relative
                    state.edit().putString(cursorKey, lastProcessedPath).putInt(processedKey, processed).putInt(storedKey, stored).apply()
                    client.progress(config, runId, processed, files.size, relative, "업로드 중")
                    setProgress(workDataOf("stage" to "업로드 중", "processed" to processed, "total" to files.size, "currentFile" to relative))
                } finally { temp.delete() }
            }
            val finished = firstIndex >= files.size || (lastProcessedPath.isNotBlank() && files.drop(firstIndex).lastOrNull()?.second == lastProcessedPath)
            if (finished) {
                client.finish(config, runId, processed, stored)
                if (androidx.core.content.ContextCompat.checkSelfPermission(applicationContext, android.Manifest.permission.READ_CONTACTS) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
                    setProgress(workDataOf("stage" to "주소록 동기화 중", "processed" to processed, "total" to files.size))
                    client.downloadContacts(config).forEach { ContactsRepository(applicationContext).upsertManaged(it) }
                }
                state.edit().remove(runKey).remove(cursorKey).remove(processedKey).remove(storedKey).apply()
                Result.success(workDataOf("processed" to processed, "total" to files.size, "stored" to stored))
            } else {
                client.progress(config, runId, processed, files.size, lastProcessedPath, "다음 분할 백업 대기")
                Result.retry()
            }
        } catch (e: Exception) {
            val transient = e is IOException || e is SocketException || e is UnknownHostException || e is SSLException
            if (runId.isNotEmpty() && !transient) runCatching { client.finish(config, runId, processed, stored, e.message ?: e::class.simpleName) }
            if (transient) Result.retry() else {
                state.edit().remove(runKey).remove(cursorKey).remove(processedKey).remove(storedKey).apply()
                Result.failure(workDataOf("error" to (e.message ?: e::class.simpleName ?: "알 수 없는 오류"), "processed" to processed, "stored" to stored))
            }
        }
    }
}

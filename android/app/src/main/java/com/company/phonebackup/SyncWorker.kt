package com.company.phonebackup

import android.content.Context
import android.net.Uri
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import androidx.work.workDataOf

class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
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
        val client = NetworkClient(applicationContext, store); val repository = FileRepository(applicationContext); var seen = 0; var stored = 0; var runId = ""
        return try {
            setProgress(workDataOf("stage" to "PC 연결 중", "processed" to 0, "total" to 0))
            runId = client.start(config)
            if (androidx.core.content.ContextCompat.checkSelfPermission(applicationContext, android.Manifest.permission.READ_CONTACTS) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
                setProgress(workDataOf("stage" to "주소록 백업 중", "processed" to 0, "total" to 0))
                runCatching { client.proposeContacts(config, ContactsRepository(applicationContext).readAll()) }
            }
            setProgress(workDataOf("stage" to "파일 검색 중", "processed" to 0, "total" to 0))
            val files = if (tree.isNullOrBlank()) repository.listDefaultFiles(config.deviceId) else repository.listFiles(Uri.parse(tree))
            client.progress(config, runId, 0, files.size, null, "백업 준비")
            files.forEach { (uri, relative) ->
                setProgress(workDataOf("stage" to "업로드 중", "processed" to seen, "total" to files.size, "currentFile" to relative))
                client.progress(config, runId, seen, files.size, relative, "업로드 중")
                val temp = repository.copyToTemp(uri)
                try {
                    val sha = client.sha256ForUpload(temp)
                    if (!client.isKnown(config, relative, sha)) {
                        client.upload(config, runId, temp, relative, categoryFor(relative), sha)
                    }
                    stored++
                } finally { temp.delete() }
                seen++
                setProgress(workDataOf("stage" to "업로드 중", "processed" to seen, "total" to files.size, "currentFile" to relative))
                client.progress(config, runId, seen, files.size, relative, "업로드 중")
            }
            client.finish(config, runId, seen, stored)
            if (androidx.core.content.ContextCompat.checkSelfPermission(applicationContext, android.Manifest.permission.READ_CONTACTS) == android.content.pm.PackageManager.PERMISSION_GRANTED) {
                setProgress(workDataOf("stage" to "주소록 동기화 중", "processed" to seen, "total" to files.size))
                client.downloadContacts(config).forEach { ContactsRepository(applicationContext).upsertManaged(it) }
            }
            Result.success(workDataOf("processed" to seen, "total" to files.size, "stored" to stored))
        } catch (e: Exception) {
            if (runId.isNotEmpty()) runCatching { client.finish(config, runId, seen, stored, e.message ?: e::class.simpleName) }
            Result.failure(workDataOf("error" to (e.message ?: e::class.simpleName ?: "알 수 없는 오류"), "processed" to seen, "stored" to stored))
        }
    }
}

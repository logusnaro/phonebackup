package com.company.phonebackup

import android.content.Context
import android.net.Uri
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import androidx.work.workDataOf

class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
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
                    client.upload(config, runId, temp, relative, if (relative.contains("record", true) || relative.contains("call", true) || relative.endsWith(".m4a", true)) "recording" else "file")
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

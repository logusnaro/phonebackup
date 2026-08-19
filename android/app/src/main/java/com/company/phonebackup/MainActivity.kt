package com.company.phonebackup

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Bundle
import android.os.Build
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import kotlinx.serialization.json.Json
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkInfo
import androidx.work.ExistingWorkPolicy
import kotlinx.coroutines.delay

class MainActivity : ComponentActivity() {
    private lateinit var store: PairingStore
    private val status = mutableStateOf("PC와 연결되지 않았습니다.")
    private val connectionStatus = mutableStateOf("PC 연결 확인 중…")
    private val backupStage = mutableStateOf("")
    private val backupProcessed = mutableIntStateOf(0)
    private val backupTotal = mutableIntStateOf(0)
    private val backupRunning = mutableStateOf(false)
    private val backupCurrentFile = mutableStateOf("")
    private var treeUri: Uri? = null
    private val folderPicker = registerForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri -> uri?.let { treeUri = it; contentResolver.takePersistableUriPermission(it, Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION); getSharedPreferences("sync", MODE_PRIVATE).edit().putString("treeUri", it.toString()).apply() } }
    private val usbPairingFile = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri ?: return@registerForActivityResult
        status.value = "USB 등록 파일을 읽는 중…"
        Thread {
            try {
                val text = contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }
                    ?: error("등록 파일을 읽을 수 없습니다.")
                val ticket = Json { ignoreUnknownKeys = true }.decodeFromString(UsbPairingFile.serializer(), text)
                val response = NetworkClient(this@MainActivity, store).pair(ticket.toTicket())
                store.save(PairingConfig(response.deviceId, response.deviceToken, response.serverUrl, response.certificateSha256))
                runOnUiThread { status.value = "USB 등록 완료 · 이제 USB를 분리하고 Wi‑Fi로 사용할 수 있습니다." }
            } catch (e: Exception) {
                Log.e("PhoneBackup", "USB pairing failed", e)
                runOnUiThread { status.value = "USB 등록 실패: ${e::class.simpleName}: ${e.message}" }
            }
        }.start()
    }
    private val permissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { }
    private val storagePermissions = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { result ->
        status.value = if (result.values.all { it }) "표준 폴더 자동 검색을 사용할 수 있습니다." else "저장공간 권한이 없어 직접 폴더 선택이 필요합니다."
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        store = PairingStore(this)
        val migrations = getSharedPreferences("migrations", MODE_PRIVATE)
        if (!migrations.getBoolean("cancelLegacySlowFolderWorkV2", false)) {
            WorkManager.getInstance(this).cancelAllWork()
            migrations.edit().putBoolean("cancelLegacyDuplicateWorkV1", true).putBoolean("cancelLegacySlowFolderWorkV2", true).apply()
            status.value = "이전 중복 백업 작업을 정리했습니다. ‘지금 백업’을 한 번 눌러 주세요."
        } else status.value = if (store.load() == null) "PC와 연결되지 않았습니다." else "등록된 PC가 있습니다."
        setContent { PhoneBackupScreen() }
        requestStorageAccess(false)
    }
    private fun startBackup() {
        val request = OneTimeWorkRequestBuilder<SyncWorker>().build()
        backupRunning.value = true; backupStage.value = "백업 시작 대기"; backupProcessed.intValue = 0; backupTotal.intValue = 0
        WorkManager.getInstance(this).enqueueUniqueWork("manual-backup", ExistingWorkPolicy.KEEP, request)
        WorkManager.getInstance(this).getWorkInfoByIdLiveData(request.id).observe(this) { info ->
            if (info == null) return@observe
            val data = if (info.state == WorkInfo.State.SUCCEEDED || info.state == WorkInfo.State.FAILED) info.outputData else info.progress
            backupProcessed.intValue = data.getInt("processed", backupProcessed.intValue)
            backupTotal.intValue = data.getInt("total", backupTotal.intValue)
            backupCurrentFile.value = data.getString("currentFile") ?: ""
            backupStage.value = when (info.state) {
                WorkInfo.State.SUCCEEDED -> "백업 완료 · ${data.getInt("stored", 0)}개 저장"
                WorkInfo.State.FAILED -> "백업 실패: ${data.getString("error") ?: "원인을 확인할 수 없습니다."}"
                WorkInfo.State.CANCELLED -> "백업 취소됨"
                else -> data.getString("stage") ?: "백업 대기 중"
            }
            backupRunning.value = !info.state.isFinished
        }
    }
    private fun requestStorageAccess(showPickerFallback: Boolean = true) {
        if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.P) {
            val missing = arrayOf(Manifest.permission.READ_EXTERNAL_STORAGE, Manifest.permission.WRITE_EXTERNAL_STORAGE).filter { ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
            if (missing.isNotEmpty()) { storagePermissions.launch(missing.toTypedArray()); return }
            status.value = "표준 폴더를 자동 검색합니다."
        } else if (showPickerFallback) {
            status.value = "Android 10 이상에서는 접근할 폴더를 한 번 선택해 주세요."
            folderPicker.launch(null)
        }
    }
    private fun useDefaultFolders() {
        treeUri = null
        getSharedPreferences("sync", MODE_PRIVATE).edit().remove("treeUri").apply()
        store.load()?.deviceId?.let { FileRepository(this).clearSavedProfile(it) }
        status.value = "다음 백업에서 이 기기의 저장 폴더를 다시 자동 검색합니다."
        requestStorageAccess(false)
    }
    @Composable private fun PhoneBackupScreen() {
        var pairingCode by remember { mutableStateOf("") }
        LaunchedEffect(Unit) {
            while (true) {
                val config = store.load()
                connectionStatus.value = if (backupRunning.value) {
                    "● PC 사용 가능 · 백업 진행 중"
                } else if (config == null) {
                    "● PC 미등록"
                } else try {
                    NetworkClient(this@MainActivity, store).ping(config)
                    "● PC 사용 가능"
                } catch (e: Exception) {
                    "● PC 등록됨 · 백업 시 Wi‑Fi로 자동 연결"
                }
                delay(5000)
            }
        }
        Column(Modifier.fillMaxSize().padding(22.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Text("업무폰 백업", style = MaterialTheme.typography.headlineMedium); Text(connectionStatus.value, color = if (connectionStatus.value.contains("사용 가능") || connectionStatus.value.contains("등록됨")) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error); Text("원본 파일은 변경하지 않고 PC로 복사합니다.")
            Text("처음 연결은 USB 등록 파일로 진행하거나, 같은 Wi‑Fi에서 6자리 코드로 연결할 수 있습니다.", style = MaterialTheme.typography.bodySmall)
            OutlinedTextField(pairingCode, { pairingCode = it.filter { c -> c.isLetterOrDigit() }.take(6).uppercase() }, Modifier.fillMaxWidth(), label = { Text("연결 코드 6자리") }, singleLine = true)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = {
                    status.value = "같은 Wi‑Fi에서 PC를 찾는 중…"
                    Thread {
                        try {
                            val response = NetworkClient(this@MainActivity, store).discoverAndPair(pairingCode)
                            store.save(PairingConfig(response.deviceId, response.deviceToken, response.serverUrl, response.certificateSha256))
                            runOnUiThread { status.value = "PC 연결 완료" }
                        } catch (e: Exception) {
                            Log.e("PhoneBackup", "pairing failed", e)
                            runOnUiThread { status.value = "연결 실패: ${e::class.simpleName}: ${e.message}" }
                        }
                    }.start()
                }, enabled = pairingCode.length == 6) { Text("PC 찾기") }
            }
            OutlinedButton(onClick = { usbPairingFile.launch(arrayOf("application/json", "text/plain", "*/*")) }, modifier = Modifier.fillMaxWidth()) { Text("USB 등록하기") }
            OutlinedButton(onClick = { useDefaultFolders() }, modifier = Modifier.fillMaxWidth()) { Text("표준 폴더 자동 검색") }
            TextButton(onClick = { folderPicker.launch(null) }, modifier = Modifier.fillMaxWidth()) { Text("직접 폴더 선택(필요한 경우)") }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) { OutlinedButton(onClick = { permissions.launch(arrayOf(Manifest.permission.READ_CONTACTS, Manifest.permission.WRITE_CONTACTS)) }) { Text("주소록 권한") }; Button(onClick = { startBackup() }, enabled = !backupRunning.value) { Text(if (backupRunning.value) "백업 중…" else "지금 백업") } }
            if (backupRunning.value || backupStage.value.isNotBlank()) {
                val total = backupTotal.intValue
                if (total > 0) LinearProgressIndicator(progress = { backupProcessed.intValue.toFloat() / total }, modifier = Modifier.fillMaxWidth())
                else LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                Text("${backupStage.value}${if (total > 0) " · ${backupProcessed.intValue}/$total (${backupProcessed.intValue * 100 / total}%)" else ""}", style = MaterialTheme.typography.bodyMedium)
                if (backupCurrentFile.value.isNotBlank()) Text("현재 파일: ${backupCurrentFile.value}", style = MaterialTheme.typography.bodySmall, maxLines = 2)
            }
            Text(status.value, color = MaterialTheme.colorScheme.primary); Text("예약은 PC 앱에서 활성화한 뒤 Android가 Wi‑Fi에서 실행합니다.", style = MaterialTheme.typography.bodySmall)
        }
    }
}

@kotlinx.serialization.Serializable
data class UsbPairingFile(
    val version: Int = 1,
    val ticketId: String,
    val serverUrl: String,
    val certificateSha256: String
) {
    fun toTicket() = PairingTicketInput(ticketId, serverUrl, certificateSha256)
}

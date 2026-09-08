package com.company.phonebackup

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Environment
import android.os.StatFs
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.UUID
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

private val PbGreen = Color(0xFF16835A)
private val PbGreenDark = Color(0xFF0F5F42)
private val PbGreenSoft = Color(0xFFE8F5EF)
private val PbCanvas = Color(0xFFF5F7F6)
private val PbInk = Color(0xFF18211D)
private val PbMuted = Color(0xFF64716A)
private val PbDanger = Color(0xFFB42318)

class MainActivity : ComponentActivity() {
    private val repository by lazy { FileRepository(this) }
    private val prefs by lazy { getSharedPreferences("pb_local_manager", MODE_PRIVATE) }
    private var files by mutableStateOf<List<MobileFile>>(emptyList())
    private var scanning by mutableStateOf(false)
    private var scanMessage by mutableStateOf("파일 확인 전")
    private var authorizationUntil by mutableLongStateOf(0L)
    private var snapshotCreatedAt by mutableLongStateOf(0L)
    private var storage by mutableStateOf(StorageStatus(0, 0, 0))
    private var pendingDiagnostic: ByteArray? = null
    private val pendingDeleteBatches = ArrayDeque<List<MobileFile>>()
    private var currentDeleteBatchSize = 0
    private var systemDeletedApproved = 0
    private var deletedBeforeSystemApproval = 0
    private var failedBeforeSystemApproval = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val systemExceptionHandler = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            runCatching { FileLogger.append(this, "fatal", error) }
            if (systemExceptionHandler != null) {
                systemExceptionHandler.uncaughtException(thread, error)
            } else {
                android.os.Process.killProcess(android.os.Process.myPid())
            }
        }
        authorizationUntil = prefs.getLong("authorization_until", 0L)
        snapshotCreatedAt = prefs.getLong("snapshot_created_at", 0L)
        ReminderWorker.schedule(this)
        requestStoragePermissions()
        refreshStorage()
        scanFiles()
        setContent { PbTheme { PhoneBackupApp() } }
    }

    private fun requestStoragePermissions() {
        val requested = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= 33) {
            requested += Manifest.permission.READ_MEDIA_AUDIO
            requested += Manifest.permission.READ_MEDIA_IMAGES
            requested += Manifest.permission.READ_MEDIA_VIDEO
            requested += Manifest.permission.POST_NOTIFICATIONS
        } else {
            requested += Manifest.permission.READ_EXTERNAL_STORAGE
            if (Build.VERSION.SDK_INT <= 28) requested += Manifest.permission.WRITE_EXTERNAL_STORAGE
        }
        val missing = requested.filter { ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED }
        if (missing.isNotEmpty()) registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { scanFiles() }
            .launch(missing.toTypedArray())
    }

    private fun selectedTrees(): Set<String> = prefs.getStringSet("selected_trees", emptySet())?.toSet().orEmpty()

    private fun addTree(uri: Uri) {
        runCatching {
            contentResolver.takePersistableUriPermission(uri,
                Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION)
        }
        prefs.edit().putStringSet("selected_trees", selectedTrees() + uri.toString()).apply()
        scanFiles()
    }

    private fun scanFiles(after: (() -> Unit)? = null) {
        if (scanning) return
        scanning = true; scanMessage = "휴대폰 파일을 확인하는 중…"
        lifecycleScope.launch {
            runCatching { withContext(Dispatchers.IO) { repository.scan(selectedTrees()) } }
                .onSuccess {
                    files = it
                    scanMessage = "${it.size.format()}개 파일 확인 · ${Date().shortTime()}"
                    refreshStorage()
                    after?.invoke()
                }
                .onFailure {
                    FileLogger.append(this@MainActivity, "scan", it)
                    scanMessage = "일부 폴더를 읽지 못했습니다: ${it.javaClass.simpleName}"
                }
            scanning = false
        }
    }

    private fun refreshStorage() {
        runCatching {
            val stat = StatFs(Environment.getDataDirectory().path)
            val total = stat.totalBytes
            val free = stat.availableBytes
            storage = StorageStatus(total, total - free, free)
        }
    }

    private fun createSnapshot() {
        if (files.isEmpty()) { scanMessage = "먼저 파일 검사를 완료해 주세요."; return }
        val keys = files.map { it.stableKey.sha256() }.toSet()
        val now = System.currentTimeMillis()
        prefs.edit().putStringSet("snapshot_keys", keys).putLong("snapshot_created_at", now)
            .remove("authorization_until").apply()
        snapshotCreatedAt = now; authorizationUntil = 0L
    }

    private fun confirmBackupCompleted() {
        val until = System.currentTimeMillis() + 24L * 60 * 60 * 1000
        prefs.edit().putLong("authorization_until", until).apply()
        authorizationUntil = until
    }

    private fun isDeletionEligible(file: MobileFile): Boolean {
        if (System.currentTimeMillis() >= authorizationUntil) return false
        val oldEnoughWhenRecording = !file.isCallRecording ||
            (file.modifiedAtMillis > 0 && file.modifiedAtMillis < System.currentTimeMillis() - 90L * 24 * 60 * 60 * 1000)
        return oldEnoughWhenRecording && prefs.getStringSet("snapshot_keys", emptySet())?.contains(file.stableKey.sha256()) == true
    }

    private fun startDelete(
        items: List<MobileFile>,
        enforceBackupGate: Boolean = true,
        onComplete: (String) -> Unit
    ) {
        val eligible = if (enforceBackupGate) {
            items.filter(::isDeletionEligible)
        } else {
            // 사용자가 백업 미확인 경고에서 다시 승인한 항목만 우회한다.
            items
        }
        if (eligible.isEmpty()) {
            onComplete(if (enforceBackupGate) "삭제 가능한 파일이 없습니다. 백업 확인 상태를 점검하세요." else "삭제할 파일이 없습니다.")
            return
        }
        lifecycleScope.launch {
            val direct = eligible.filter { it.canDeleteDirectly || it.uri.scheme == "file" }
            val remaining = eligible - direct.toSet()
            val system = remaining.filter(repository::supportsSystemDelete)
            val unsupportedCount = remaining.size - system.size
            val directResult = withContext(Dispatchers.IO) { repository.deleteDirect(direct) }
            if (system.isNotEmpty() && Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                pendingDeleteBatches.clear()
                system.chunked(1500).forEach { pendingDeleteBatches.addLast(it) }
                if (pendingDeleteBatches.isNotEmpty()) {
                    systemDeletedApproved = 0
                    deletedBeforeSystemApproval = directResult.first
                    failedBeforeSystemApproval = directResult.second + unsupportedCount
                    launchNextDeleteBatch()
                    onComplete("${system.size}개 파일을 Android 확인창에서 승인해 주세요." +
                        if (unsupportedCount > 0) " · 권한 없는 ${unsupportedCount}개 제외" else "")
                    return@launch
                }
            }
            val legacyResult = withContext(Dispatchers.IO) { repository.deleteDirect(system) }
            onComplete("삭제 ${directResult.first + legacyResult.first}개 · 실패 ${directResult.second + legacyResult.second + unsupportedCount}개")
            scanFiles()
        }
    }

    private val systemDeleteLauncher = registerForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) {
            systemDeletedApproved += currentDeleteBatchSize
            if (pendingDeleteBatches.isNotEmpty()) launchNextDeleteBatch()
            else {
                scanMessage = "삭제 ${deletedBeforeSystemApproval + systemDeletedApproved}개 · 실패 ${failedBeforeSystemApproval}개"
                scanFiles()
            }
        } else {
            pendingDeleteBatches.clear(); currentDeleteBatchSize = 0
            scanMessage = if (deletedBeforeSystemApproval > 0 || failedBeforeSystemApproval > 0) {
                "Android 승인 취소 · 이미 삭제 ${deletedBeforeSystemApproval}개 · 실패 ${failedBeforeSystemApproval}개"
            } else {
                "삭제를 취소했습니다."
            }
            scanFiles()
        }
    }

    private fun launchNextDeleteBatch() {
        val batch = pendingDeleteBatches.removeFirstOrNull() ?: return
        runCatching {
            repository.createSystemDeleteRequest(batch)
                ?: error("Android 삭제 승인 요청을 만들지 못했습니다.")
        }.onSuccess { request ->
            runCatching {
                currentDeleteBatchSize = batch.size
                systemDeleteLauncher.launch(IntentSenderRequest.Builder(request.intentSender).build())
            }.onFailure(::handleDeleteRequestFailure)
        }.onFailure(::handleDeleteRequestFailure)
    }

    private fun handleDeleteRequestFailure(error: Throwable) {
        FileLogger.append(this, "delete-request", error)
        pendingDeleteBatches.clear()
        currentDeleteBatchSize = 0
        scanMessage = "삭제 요청 실패 · 삭제 ${deletedBeforeSystemApproval}개 · 실패 ${failedBeforeSystemApproval + 1}개"
    }

    private fun openSmartSwitch(): Boolean {
        val packages = listOf("com.sec.android.easyMover", "com.samsung.android.smartswitchassistant", "com.samsung.android.easysetup")
        val launch = packages.firstNotNullOfOrNull { packageManager.getLaunchIntentForPackage(it) }
        return if (launch != null) { startActivity(launch); true } else false
    }

    @OptIn(ExperimentalMaterial3Api::class)
    @Composable
    private fun PhoneBackupApp() {
        var page by rememberSaveable { mutableIntStateOf(0) }
        var showBackupGuide by remember { mutableStateOf(false) }
        var showCompleteCheck by remember { mutableStateOf(false) }
        var showErrorExport by remember { mutableStateOf(false) }
        val folderPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { it?.let(::addTree) }
        val diagnosticSaver = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/zip")) { uri ->
            val bytes = pendingDiagnostic; pendingDiagnostic = null
            if (uri != null && bytes != null) runCatching { contentResolver.openOutputStream(uri)?.use { it.write(bytes) } }
        }

        Scaffold(
            containerColor = PbCanvas,
            topBar = {
                TopAppBar(title = { Column { Text("PB", fontWeight = FontWeight.Bold); Text("휴대폰 저장공간 관리", fontSize = 12.sp, color = PbMuted) } },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.White),
                    actions = { TextButton(onClick = { showErrorExport = true }) { Text("도움말", color = PbGreenDark) } })
            },
            bottomBar = {
                NavigationBar(containerColor = Color.White) {
                    listOf("현황", "통화녹음", "파일 정리").forEachIndexed { index, label ->
                        NavigationBarItem(selected = page == index, onClick = { page = index },
                            icon = { Text(listOf("●", "◉", "▦")[index]) }, label = { Text(label) },
                            colors = NavigationBarItemDefaults.colors(selectedIconColor = PbGreen, selectedTextColor = PbGreen,
                                indicatorColor = PbGreenSoft))
                    }
                }
            }
        ) { padding ->
            when (page) {
                0 -> OverviewScreen(Modifier.padding(padding), onScan = { scanFiles() }, onPrepare = { createSnapshot(); showBackupGuide = true },
                    onConfirm = { showCompleteCheck = true }, onFolder = { folderPicker.launch(null) })
                1 -> RecordingScreen(Modifier.padding(padding))
                else -> GeneralFilesScreen(Modifier.padding(padding), onFolder = { folderPicker.launch(null) })
            }
        }

        if (showBackupGuide) AlertDialog(onDismissRequest = { showBackupGuide = false },
            title = { Text("백업 준비가 끝났습니다") },
            text = { Text("현재 ${files.size.format()}개 파일 목록을 고정했습니다.\n\n1. USB로 PC 연결\n2. Smart Switch에서 백업 시작\n3. 백업 완료 표시 확인\n4. PB로 돌아와 ‘백업 완료 확인’\n\nPB는 PC와 통신하거나 백업 성공 여부를 자동 판정하지 않습니다.") },
            confirmButton = { Button(onClick = { openSmartSwitch(); showBackupGuide = false }, colors = ButtonDefaults.buttonColors(containerColor = PbGreen)) { Text("Smart Switch 열기") } },
            dismissButton = { TextButton(onClick = { showBackupGuide = false }) { Text("나중에") } })

        if (showCompleteCheck) BackupCompletionDialog(onDismiss = { showCompleteCheck = false }, onConfirm = { confirmBackupCompleted(); showCompleteCheck = false })

        if (showErrorExport) AlertDialog(onDismissRequest = { showErrorExport = false }, title = { Text("도움말 · 오류 대응") },
            text = { Text("PB는 PC 연결 없이 휴대폰 안에서만 작동합니다. 파일이 안 보이면 저장소 권한을 확인하고 ‘검토 폴더 추가’를 이용하세요.\n\n오류 리포트에는 파일 내용과 원본 파일명이 포함되지 않습니다.") },
            confirmButton = { TextButton(onClick = { pendingDiagnostic = buildDiagnostic(); showErrorExport = false; diagnosticSaver.launch("PB-error-report-${Date().fileStamp()}.zip") }) { Text("오류 리포트 저장") } },
            dismissButton = { TextButton(onClick = { showErrorExport = false }) { Text("닫기") } })
    }

    @Composable
    private fun OverviewScreen(modifier: Modifier, onScan: () -> Unit, onPrepare: () -> Unit, onConfirm: () -> Unit, onFolder: () -> Unit) {
        val threshold = prefs.getInt("warning_threshold", 85)
        val usedPercent = storage.usedPercent
        val authorized = System.currentTimeMillis() < authorizationUntil
        LazyColumn(modifier.fillMaxSize(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            item {
                PbCard(background = if (usedPercent >= threshold) Color(0xFFFFF1EF) else Color.White) {
                    Text(if (usedPercent >= threshold) "저장공간 정리가 필요합니다" else "저장공간 상태가 안정적입니다", fontSize = 20.sp, fontWeight = FontWeight.Bold,
                        color = if (usedPercent >= threshold) PbDanger else PbInk)
                    Spacer(Modifier.height(12.dp)); LinearProgressIndicator(progress = { usedPercent / 100f }, modifier = Modifier.fillMaxWidth().height(10.dp), color = if (usedPercent >= threshold) PbDanger else PbGreen, trackColor = Color(0xFFE1E7E3))
                    Row(Modifier.fillMaxWidth().padding(top = 10.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text("사용 ${storage.used.humanSize()} · ${usedPercent}%", fontWeight = FontWeight.SemiBold)
                        Text("여유 ${storage.free.humanSize()}", color = PbMuted)
                    }
                }
            }
            item {
                PbCard {
                    Text("안전한 정리 순서", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                    StepRow("1", "현재 파일 목록 고정", snapshotCreatedAt > 0)
                    StepRow("2", "Smart Switch 백업", false)
                    StepRow("3", "백업 완료 직접 확인", authorized)
                    Spacer(Modifier.height(10.dp))
                    Button(onClick = onPrepare, modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.buttonColors(containerColor = PbGreen)) { Text("백업 준비 시작") }
                    if (snapshotCreatedAt > 0 && !authorized) OutlinedButton(onClick = onConfirm, modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) { Text("백업 완료 확인") }
                    if (authorized) Text("삭제 잠금 해제 · ${Date(authorizationUntil).shortTime()}까지", color = PbGreenDark, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 10.dp))
                }
            }
            item {
                PbCard {
                    Row(verticalAlignment = Alignment.CenterVertically) { Column(Modifier.weight(1f)) { Text("파일 검사", fontWeight = FontWeight.Bold); Text(scanMessage, color = PbMuted, fontSize = 13.sp) }; if (scanning) CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp, color = PbGreen) else TextButton(onClick = onScan) { Text("다시 검사") } }
                    HorizontalDivider(Modifier.padding(vertical = 12.dp), color = Color(0xFFE6ECE8))
                    SummaryRow("통화녹음", files.count { it.isCallRecording }, files.filter { it.isCallRecording }.sumOf { it.sizeBytes })
                    SummaryRow("사진", files.count { it.category == "image" }, files.filter { it.category == "image" }.sumOf { it.sizeBytes })
                    SummaryRow("영상", files.count { it.category == "video" }, files.filter { it.category == "video" }.sumOf { it.sizeBytes })
                    SummaryRow("문서·기타", files.count { it.category in setOf("document", "other", "audio") }, files.filter { it.category in setOf("document", "other", "audio") }.sumOf { it.sizeBytes })
                }
            }
            item {
                PbCard { Text("검토 범위", fontWeight = FontWeight.Bold); Text("Android가 허용한 사진·영상·오디오와 직접 선택한 폴더를 검사합니다.", color = PbMuted, fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp)); OutlinedButton(onClick = onFolder, modifier = Modifier.fillMaxWidth()) { Text("검토 폴더 추가 (${selectedTrees().size})") } }
            }
            item { ThresholdSelector(threshold) }
        }
    }

    @Composable
    private fun RecordingScreen(modifier: Modifier) {
        val cutoff = System.currentTimeMillis() - 90L * 24 * 60 * 60 * 1000
        val candidates = files.filter { it.isCallRecording && it.modifiedAtMillis > 0 && it.modifiedAtMillis < cutoff }.sortedBy { it.modifiedAtMillis }
        var selected by remember(candidates) { mutableStateOf(setOf<String>()) }
        var confirmDelete by remember { mutableStateOf(false) }
        var message by remember { mutableStateOf("") }
        val authorized = System.currentTimeMillis() < authorizationUntil
        Column(modifier.fillMaxSize().padding(16.dp)) {
            PbCard(background = if (authorized) PbGreenSoft else Color(0xFFFFF7E8)) {
                Text("90일 지난 통화녹음", fontSize = 19.sp, fontWeight = FontWeight.Bold)
                Text("${candidates.size.format()}개 · ${candidates.sumOf { it.sizeBytes }.humanSize()}", color = PbMuted, modifier = Modifier.padding(top = 4.dp))
                Text(if (authorized) "백업 확인 완료 · 목록에 고정된 파일만 삭제 가능" else "백업 미확인 상태에서도 경고 확인 후 삭제 가능", color = if (authorized) PbGreenDark else Color(0xFF8A5A00), fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
            }
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = candidates.isNotEmpty() && selected.size == candidates.size, onCheckedChange = { checked -> selected = if (checked) candidates.map { it.stableKey }.toSet() else emptySet() })
                Text("전체 선택", Modifier.weight(1f)); Text("선택 ${selected.size}개", color = PbMuted)
            }
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                items(candidates, key = { it.stableKey }) { item -> FileRow(item, selected.contains(item.stableKey), { checked -> selected = if (checked) selected + item.stableKey else selected - item.stableKey }, false) }
            }
            if (message.isNotBlank()) Text(message, color = PbMuted, fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp))
            Button(onClick = { confirmDelete = true }, enabled = selected.isNotEmpty(), modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.buttonColors(containerColor = PbDanger)) { Text("선택한 통화녹음 삭제") }
        }
        if (confirmDelete) {
            val selectedFiles = candidates.filter { it.stableKey in selected }
            if (authorized) {
                DeleteConfirmDialog(selected.size, onDismiss = { confirmDelete = false }, onConfirm = {
                    confirmDelete = false
                    startDelete(selectedFiles) { message = it; selected = emptySet() }
                })
            } else {
                UnverifiedDeleteConfirmDialog(selected.size, onDismiss = { confirmDelete = false }, onConfirm = {
                    confirmDelete = false
                    startDelete(selectedFiles, enforceBackupGate = false) { message = it; selected = emptySet() }
                })
            }
        }
    }

    @Composable
    private fun GeneralFilesScreen(modifier: Modifier, onFolder: () -> Unit) {
        val categories = listOf("image" to "사진", "video" to "영상", "document" to "문서", "audio" to "오디오", "other" to "기타")
        var category by rememberSaveable { mutableStateOf("image") }
        val shown = files.filter { !it.isCallRecording && it.category == category }.sortedByDescending { it.modifiedAtMillis }
        var selected by remember(category, shown) { mutableStateOf(setOf<String>()) }
        var confirmDelete by remember { mutableStateOf(false) }
        var message by remember { mutableStateOf("") }
        val authorized = System.currentTimeMillis() < authorizationUntil
        Column(modifier.fillMaxSize().padding(16.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) { Column(Modifier.weight(1f)) { Text("파일 정리", fontSize = 20.sp, fontWeight = FontWeight.Bold); Text("직접 보고 여러 개를 선택하세요", color = PbMuted, fontSize = 13.sp) }; OutlinedButton(onClick = onFolder) { Text("폴더 추가") } }
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                categories.forEach { (id, label) -> FilterChip(selected = category == id, onClick = { category = id }, label = { Text(label) }, colors = FilterChipDefaults.filterChipColors(selectedContainerColor = PbGreenSoft, selectedLabelColor = PbGreenDark)) }
            }
            Row(verticalAlignment = Alignment.CenterVertically) { Checkbox(checked = shown.isNotEmpty() && selected.size == shown.size, onCheckedChange = { selected = if (it) shown.map { file -> file.stableKey }.toSet() else emptySet() }); Text("전체 선택", Modifier.weight(1f)); Text("${shown.size.format()}개 · ${shown.sumOf { it.sizeBytes }.humanSize()}", color = PbMuted) }
            LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(8.dp)) { items(shown, key = { it.stableKey }) { item -> FileRow(item, item.stableKey in selected, { checked -> selected = if (checked) selected + item.stableKey else selected - item.stableKey }, category == "image") } }
            if (!authorized) Text("백업 미확인 상태에서도 삭제할 수 있지만, 삭제 직전에 다시 확인합니다.", color = Color(0xFF8A5A00), fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp))
            if (message.isNotBlank()) Text(message, color = PbMuted, fontSize = 13.sp, modifier = Modifier.padding(vertical = 6.dp))
            Button(onClick = { confirmDelete = true }, enabled = selected.isNotEmpty(), modifier = Modifier.fillMaxWidth(), colors = ButtonDefaults.buttonColors(containerColor = PbDanger)) { Text("선택한 파일 삭제") }
        }
        if (confirmDelete) {
            val selectedFiles = shown.filter { it.stableKey in selected }
            if (authorized) {
                DeleteConfirmDialog(selected.size, onDismiss = { confirmDelete = false }, onConfirm = {
                    confirmDelete = false
                    startDelete(selectedFiles) { message = it; selected = emptySet() }
                })
            } else {
                UnverifiedDeleteConfirmDialog(selected.size, onDismiss = { confirmDelete = false }, onConfirm = {
                    confirmDelete = false
                    startDelete(selectedFiles, enforceBackupGate = false) { message = it; selected = emptySet() }
                })
            }
        }
    }

    @Composable
    private fun FileRow(item: MobileFile, checked: Boolean, onChecked: (Boolean) -> Unit, preview: Boolean) {
        Surface(shape = RoundedCornerShape(12.dp), color = Color.White, tonalElevation = 0.dp, shadowElevation = 1.dp,
            modifier = Modifier.fillMaxWidth().clickable { onChecked(!checked) }) {
            Row(Modifier.padding(10.dp), verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = checked, onCheckedChange = onChecked)
                if (preview) ImageThumbnail(item)
                Column(Modifier.weight(1f).padding(start = 8.dp)) {
                    Text(item.name, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium)
                    Text("${item.dateText} · ${item.sizeBytes.humanSize()}", color = PbMuted, fontSize = 12.sp)
                    Text(item.relativePath, maxLines = 1, overflow = TextOverflow.Ellipsis, color = PbMuted, fontSize = 11.sp)
                }
            }
        }
    }

    @Composable
    private fun ImageThumbnail(item: MobileFile) {
        val context = LocalContext.current
        val bitmap by produceState<androidx.compose.ui.graphics.ImageBitmap?>(null, item.uri) {
            value = withContext(Dispatchers.IO) {
                runCatching { context.contentResolver.openInputStream(item.uri)?.use { BitmapFactory.decodeStream(it)?.asImageBitmap() } }.getOrNull()
            }
        }
        Box(Modifier.size(52.dp).background(Color(0xFFE9EEEB), RoundedCornerShape(8.dp)), contentAlignment = Alignment.Center) {
            if (bitmap != null) androidx.compose.foundation.Image(bitmap!!, null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) else Text("IMG", fontSize = 10.sp, color = PbMuted)
        }
    }

    @Composable private fun PbCard(background: Color = Color.White, content: @Composable ColumnScope.() -> Unit) = Surface(shape = RoundedCornerShape(16.dp), color = background, shadowElevation = 1.dp, modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(16.dp), content = content) }
    @Composable private fun StepRow(number: String, text: String, done: Boolean) { Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) { Surface(shape = RoundedCornerShape(20.dp), color = if (done) PbGreen else Color(0xFFE7ECE9), modifier = Modifier.size(28.dp)) { Box(contentAlignment = Alignment.Center) { Text(if (done) "✓" else number, color = if (done) Color.White else PbMuted, fontWeight = FontWeight.Bold) } }; Text(text, Modifier.padding(start = 10.dp), fontWeight = FontWeight.Medium) } }
    @Composable private fun SummaryRow(label: String, count: Int, bytes: Long) { Row(Modifier.fillMaxWidth().padding(vertical = 5.dp)) { Text(label, Modifier.weight(1f)); Text("${count.format()}개 · ${bytes.humanSize()}", color = PbMuted) } }

    @Composable
    private fun ThresholdSelector(current: Int) {
        PbCard { Text("용량 위험 알림 기준", fontWeight = FontWeight.Bold); Row(Modifier.fillMaxWidth().padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) { listOf(80, 85, 90).forEach { value -> FilterChip(selected = current == value, onClick = { prefs.edit().putInt("warning_threshold", value).apply(); refreshStorage() }, label = { Text("$value%") }, colors = FilterChipDefaults.filterChipColors(selectedContainerColor = PbGreenSoft, selectedLabelColor = PbGreenDark)) } }; Text("주 1회 저장공간과 백업·정리 필요 여부를 알립니다.", color = PbMuted, fontSize = 12.sp, modifier = Modifier.padding(top = 8.dp)) }
    }

    @Composable
    private fun BackupCompletionDialog(onDismiss: () -> Unit, onConfirm: () -> Unit) {
        var finished by remember { mutableStateOf(false) }; var noError by remember { mutableStateOf(false) }; var kept by remember { mutableStateOf(false) }
        AlertDialog(onDismissRequest = onDismiss, title = { Text("백업 완료를 확인하세요") }, text = { Column { Text("PB는 PC 보안 때문에 Smart Switch 결과를 직접 확인할 수 없습니다. 아래 항목을 직접 확인해야 삭제 잠금이 풀립니다."); CheckLine("Smart Switch에 ‘백업 완료’가 표시됨", finished) { finished = it }; CheckLine("실패 또는 건너뜀 경고가 없음", noError) { noError = it }; CheckLine("PC 백업 폴더를 삭제하지 않았음", kept) { kept = it }; Text("확인 후 24시간 동안, 백업 준비 시 목록에 있던 파일만 삭제할 수 있습니다.", color = PbDanger, fontSize = 12.sp, modifier = Modifier.padding(top = 10.dp)) } }, confirmButton = { Button(onClick = onConfirm, enabled = finished && noError && kept, colors = ButtonDefaults.buttonColors(containerColor = PbGreen)) { Text("확인 완료") } }, dismissButton = { TextButton(onClick = onDismiss) { Text("취소") } })
    }

    @Composable private fun CheckLine(label: String, checked: Boolean, onChange: (Boolean) -> Unit) { Row(Modifier.fillMaxWidth().padding(top = 8.dp).clickable { onChange(!checked) }, verticalAlignment = Alignment.CenterVertically) { Checkbox(checked, onChange); Text(label) } }
    @Composable private fun DeleteConfirmDialog(count: Int, onDismiss: () -> Unit, onConfirm: () -> Unit) { AlertDialog(onDismissRequest = onDismiss, title = { Text("휴대폰에서 영구 삭제") }, text = { Text("선택한 ${count}개 파일을 휴대폰에서 삭제합니다.\n\n휴지통을 지원하지 않는 기기에서는 즉시 영구 삭제될 수 있습니다. Smart Switch 백업을 다시 확인했습니까?") }, confirmButton = { Button(onClick = onConfirm, colors = ButtonDefaults.buttonColors(containerColor = PbDanger)) { Text("삭제") } }, dismissButton = { TextButton(onClick = onDismiss) { Text("취소") } }) }

    @Composable private fun UnverifiedDeleteConfirmDialog(count: Int, onDismiss: () -> Unit, onConfirm: () -> Unit) {
        AlertDialog(
            onDismissRequest = onDismiss,
            title = { Text("백업 확인 안 됨") },
            text = { Text("선택한 ${count}개 파일의 백업 여부를 확인하지 못했습니다.\n\n백업이 안 되어 있음에도 삭제하시겠습니까? 삭제한 파일은 복구하지 못할 수 있습니다.") },
            confirmButton = { Button(onClick = onConfirm, colors = ButtonDefaults.buttonColors(containerColor = PbDanger)) { Text("확인") } },
            dismissButton = { TextButton(onClick = onDismiss) { Text("취소") } }
        )
    }

    private fun buildDiagnostic(): ByteArray {
        val output = ByteArrayOutputStream()
        ZipOutputStream(output).use { zip ->
            val report = JSONObject().apply {
                put("reportId", UUID.randomUUID().toString()); put("appVersion", BuildConfig.VERSION_NAME)
                put("androidVersion", Build.VERSION.RELEASE); put("api", Build.VERSION.SDK_INT); put("model", Build.MODEL)
                put("scanMessage", scanMessage.replace(Regex("\\d{2,}"), "#")); put("fileCount", files.size)
                put("counts", JSONObject().apply { put("recordings", files.count { it.isCallRecording }); put("images", files.count { it.category == "image" }); put("videos", files.count { it.category == "video" }); put("documents", files.count { it.category == "document" }) })
                put("selectedFolderCount", selectedTrees().size); put("snapshotExists", snapshotCreatedAt > 0); put("deletionAuthorized", System.currentTimeMillis() < authorizationUntil)
            }
            zip.putNextEntry(ZipEntry("report.json")); zip.write(report.toString(2).toByteArray()); zip.closeEntry()
            FileLogger.read(this)?.let { log -> zip.putNextEntry(ZipEntry("app.log")); zip.write(log.takeLast(100_000).toByteArray()); zip.closeEntry() }
        }
        return output.toByteArray()
    }
}

private object FileLogger {
    fun append(context: android.content.Context, stage: String, error: Throwable) { runCatching { java.io.File(context.filesDir, "phonebackup.log").appendText("${System.currentTimeMillis()} [$stage] ${error.stackTraceToString()}\n") } }
    fun read(context: android.content.Context): String? = runCatching { java.io.File(context.filesDir, "phonebackup.log").takeIf { it.exists() }?.readText() }.getOrNull()
}

private data class StorageStatus(val total: Long, val used: Long, val free: Long) { val usedPercent: Int get() = if (total <= 0) 0 else ((used * 100) / total).toInt() }
private fun Long.humanSize(): String { val units = arrayOf("B", "KB", "MB", "GB", "TB"); var value = toDouble(); var index = 0; while (value >= 1024 && index < units.lastIndex) { value /= 1024; index++ }; return "%.1f %s".format(Locale.KOREA, value, units[index]) }
private fun Int.format(): String = "%,d".format(Locale.KOREA, this)
private fun Date.shortTime(): String = SimpleDateFormat("MM-dd HH:mm", Locale.KOREA).format(this)
private fun Date.fileStamp(): String = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(this)
private fun String.sha256(): String = java.security.MessageDigest.getInstance("SHA-256")
    .digest(toByteArray()).joinToString("") { "%02x".format(it) }

@Composable
private fun PbTheme(content: @Composable () -> Unit) {
    val scheme = lightColorScheme(primary = PbGreen, onPrimary = Color.White, primaryContainer = PbGreenSoft,
        onPrimaryContainer = PbGreenDark, background = PbCanvas, surface = Color.White, onSurface = PbInk, error = PbDanger)
    MaterialTheme(colorScheme = scheme, typography = Typography(), content = content)
}

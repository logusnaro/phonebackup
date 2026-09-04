package com.company.phonebackup

import android.app.PendingIntent
import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import androidx.documentfile.provider.DocumentFile
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

data class MobileFile(
    val uri: Uri,
    val name: String,
    val relativePath: String,
    val category: String,
    val sizeBytes: Long,
    val modifiedAtMillis: Long,
    val isCallRecording: Boolean,
    val canDeleteDirectly: Boolean
) {
    val stableKey: String get() = "$relativePath|$sizeBytes|$modifiedAtMillis"
    val dateText: String get() = SimpleDateFormat("yyyy-MM-dd", Locale.KOREA).format(Date(modifiedAtMillis))
}

class FileRepository(private val context: Context) {
    private val audio = setOf("m4a", "amr", "3ga", "3gp", "wav", "mp3", "aac", "ogg", "flac")
    private val images = setOf("jpg", "jpeg", "png", "heic", "gif", "webp", "bmp")
    private val videos = setOf("mp4", "mov", "avi", "mkv", "webm", "wmv", "m4v")
    private val documents = setOf("pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "hwp", "hwpx", "txt", "csv", "zip")
    private val callFolders = setOf("tphonecallrecords", "callrecord", "callrecords", "callrecording", "callrecordings", "callar")

    fun scan(selectedTrees: Set<String>): List<MobileFile> {
        val result = linkedMapOf<String, MobileFile>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) scanMediaStore(result)
        else scanLegacyStorage(result)
        selectedTrees.forEach { value -> runCatching { scanTree(Uri.parse(value), result) } }
        return result.values.sortedByDescending { it.modifiedAtMillis }
    }

    private fun scanMediaStore(result: MutableMap<String, MobileFile>) {
        val collection = MediaStore.Files.getContentUri("external")
        val projection = arrayOf(
            MediaStore.Files.FileColumns._ID,
            MediaStore.Files.FileColumns.DISPLAY_NAME,
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.DATE_MODIFIED,
            MediaStore.Files.FileColumns.MIME_TYPE,
            MediaStore.Files.FileColumns.RELATIVE_PATH
        )
        context.contentResolver.query(collection, projection, null, null,
            "${MediaStore.Files.FileColumns.DATE_MODIFIED} DESC")?.use { cursor ->
            val idIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID)
            val nameIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME)
            val sizeIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE)
            val dateIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DATE_MODIFIED)
            val pathIndex = cursor.getColumnIndexOrThrow(MediaStore.Files.FileColumns.RELATIVE_PATH)
            while (cursor.moveToNext()) {
                val name = cursor.getString(nameIndex) ?: continue
                val folder = cursor.getString(pathIndex).orEmpty().trim('/')
                val relative = if (folder.isBlank()) name else "$folder/$name"
                val item = makeItem(ContentUris.withAppendedId(collection, cursor.getLong(idIndex)), name,
                    relative, cursor.getLong(sizeIndex), cursor.getLong(dateIndex) * 1000L, false)
                if (item.category != "other" || item.isCallRecording) result[item.stableKey] = item
            }
        }
    }

    private fun scanLegacyStorage(result: MutableMap<String, MobileFile>) {
        val root = Environment.getExternalStorageDirectory()
        val roots = listOf("Music", "TPhone", "Recordings", "callar", "DCIM", "Pictures", "Movies", "Documents", "Download", "KakaoTalkDownload", "KakaoTalk")
        roots.map { File(root, it) }.filter { it.isDirectory }.forEach { directory ->
            walkFile(directory, directory.name, result)
        }
    }

    private fun walkFile(directory: File, prefix: String, result: MutableMap<String, MobileFile>) {
        directory.listFiles()?.forEach { child ->
            if (child.name.startsWith('.') || child.name.equals("cache", true)) return@forEach
            val relative = "$prefix/${child.name}"
            if (child.isDirectory) walkFile(child, relative, result)
            else if (child.isFile && child.canRead()) {
                val item = makeItem(Uri.fromFile(child), child.name, relative, child.length(), child.lastModified(), true)
                result[item.stableKey] = item
            }
        }
    }

    private fun scanTree(treeUri: Uri, result: MutableMap<String, MobileFile>) {
        val root = DocumentFile.fromTreeUri(context, treeUri) ?: return
        var visited = 0
        fun walk(node: DocumentFile, prefix: String) {
            if (visited++ > 50_000) return
            node.listFiles().forEach { child ->
                val name = child.name.orEmpty()
                if (name.startsWith('.')) return@forEach
                val relative = if (prefix.isBlank()) name else "$prefix/$name"
                if (child.isDirectory) walk(child, relative)
                else if (child.isFile) {
                    val item = makeItem(child.uri, name, relative, child.length(), child.lastModified(), true)
                    result[item.stableKey] = item
                }
            }
        }
        walk(root, root.name.orEmpty())
    }

    private fun makeItem(uri: Uri, name: String, relative: String, size: Long, modified: Long, direct: Boolean): MobileFile {
        val ext = name.substringAfterLast('.', "").lowercase(Locale.ROOT)
        val pathParts = relative.replace('\\', '/').split('/').map { it.lowercase(Locale.ROOT) }
        val stem = name.substringBeforeLast('.')
        val recording = ext in audio && (pathParts.any { it in callFolders } ||
            Regex("(?:^|_)(?:\\+?82|0)[0-9\\- ]{8,16}_20\\d{12}$").containsMatchIn(stem))
        val category = when {
            recording -> "recording"
            ext in images -> "image"
            ext in videos -> "video"
            ext in audio -> "audio"
            ext in documents -> "document"
            else -> "other"
        }
        return MobileFile(uri, name, relative, category, size.coerceAtLeast(0), modified.coerceAtLeast(0), recording, direct)
    }

    fun deleteDirect(items: Collection<MobileFile>): Pair<Int, Int> {
        var deleted = 0; var failed = 0
        items.forEach { item ->
            val success = runCatching {
                when (item.uri.scheme) {
                    "file" -> File(item.uri.path ?: return@runCatching false).delete()
                    else -> DocumentFile.fromSingleUri(context, item.uri)?.delete() == true ||
                        context.contentResolver.delete(item.uri, null, null) > 0
                }
            }.getOrDefault(false)
            if (success) deleted++ else failed++
        }
        return deleted to failed
    }

    fun createSystemDeleteRequest(items: Collection<MobileFile>): PendingIntent? {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return null
        val uris = items.map { it.uri }.filter { it.scheme == "content" }
        if (uris.isEmpty()) return null
        return MediaStore.createDeleteRequest(context.contentResolver, uris)
    }
}

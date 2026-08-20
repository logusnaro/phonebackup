package com.company.phonebackup

import android.content.Context
import android.net.Uri
import android.provider.DocumentsContract
import androidx.documentfile.provider.DocumentFile
import java.io.File
import java.io.FileInputStream

class FileRepository(private val context: Context) {
    private val backupExtensions = setOf(
        "m4a", "amr", "3gp", "wav", "mp3", "aac", "ogg", "flac",
        "jpg", "jpeg", "png", "heic", "gif", "webp", "bmp",
        "mp4", "mov", "avi", "mkv", "wmv",
        "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx",
        "hwp", "hwpx", "cell", "show", "txt", "csv", "rtf", "zip"
    )
    private fun shouldSkip(path: String): Boolean {
        val parts = path.replace('\\', '/').split('/')
        return parts.any { it.startsWith(".") || it.equals("cache", true) } ||
            parts.lastOrNull()?.startsWith(".thumbdata", true) == true
    }
    fun listFiles(treeUri: Uri): List<Pair<Uri, String>> {
        val root = DocumentFile.fromTreeUri(context, treeUri) ?: return emptyList(); val result = mutableListOf<Pair<Uri, String>>()
        fun walk(node: DocumentFile, prefix: String) { node.listFiles().forEach { child -> val path = if (prefix.isEmpty()) child.name.orEmpty() else "$prefix/${child.name.orEmpty()}"; if (!shouldSkip(path)) { if (child.isDirectory) walk(child, path) else if (child.isFile) result.add(child.uri to path) } } }
        walk(root, ""); return result
    }
    fun listDefaultFiles(deviceId: String): List<Pair<Uri, String>> {
        val storage = android.os.Environment.getExternalStorageDirectory()
        val result = mutableListOf<Pair<Uri, String>>()
        val visitedDirectories = mutableSetOf<String>()
        fun walk(node: File, prefix: String) {
            if (!visitedDirectories.add(node.absolutePath)) return
            node.listFiles()?.forEach { child ->
                val path = if (prefix.isEmpty()) child.name else "$prefix/${child.name}"
                if (!shouldSkip(path)) {
                    if (child.isDirectory) walk(child, path)
                    else if (child.isFile && child.canRead() && child.extension.lowercase() in backupExtensions)
                        result.add(Uri.fromFile(child) to path)
                }
            }
        }
        val profiles = context.getSharedPreferences("backup_profiles", Context.MODE_PRIVATE)
        val key = "roots_$deviceId"
        val savedRoots = profiles.getStringSet(key, null)?.toSet().orEmpty()
        val candidateRoots = if (savedRoots.isNotEmpty()) {
            savedRoots.map { File(storage, it) }.filter { it.isDirectory }
        } else {
            storage.listFiles()?.filter {
                it.isDirectory && !it.name.startsWith(".") &&
                    !it.name.equals("Android", true) && !it.name.equals("LOST.DIR", true)
            }.orEmpty()
        }
        val discoveredRoots = mutableSetOf<String>()
        candidateRoots.forEach {
            val before = result.size
            walk(it, it.name)
            if (result.size > before) discoveredRoots.add(it.name)
        }
        if (savedRoots.isEmpty()) profiles.edit().putStringSet(key, discoveredRoots).apply()
        storage.listFiles()?.filter { it.isFile && it.canRead() && it.extension.lowercase() in backupExtensions }
            ?.forEach { result.add(Uri.fromFile(it) to it.name) }
        return result
    }

    fun clearSavedProfile(deviceId: String) {
        context.getSharedPreferences("backup_profiles", Context.MODE_PRIVATE)
            .edit().remove("roots_$deviceId").apply()
    }

    fun sizeOf(uri: Uri): Long {
        if (uri.scheme == "file") return File(uri.path ?: return 0L).length()
        return DocumentFile.fromSingleUri(context, uri)?.length() ?: 0L
    }

    fun deleteIfMatches(uri: Uri, expectedSha256: String): Boolean {
        val temp = copyToTemp(uri)
        return try {
            val digest = java.security.MessageDigest.getInstance("SHA-256")
            temp.inputStream().use { input ->
                val buffer = ByteArray(1024 * 1024)
                var read: Int
                while (input.read(buffer).also { read = it } > 0) digest.update(buffer, 0, read)
            }
            val actual = digest.digest().joinToString("") { "%02X".format(it) }
            if (!actual.equals(expectedSha256, ignoreCase = true)) return false
            if (uri.scheme == "file") File(uri.path ?: return false).delete()
            else DocumentFile.fromSingleUri(context, uri)?.delete() == true
        } finally { temp.delete() }
    }

    fun copyToTemp(uri: Uri): File {
        val file = File.createTempFile("phonebackup-", ".upload", context.cacheDir)
        val input = if (uri.scheme == "file") FileInputStream(uri.path!!) else context.contentResolver.openInputStream(uri)!!
        input.use { source -> file.outputStream().use { output -> source.copyTo(output) } }
        return file
    }
}
